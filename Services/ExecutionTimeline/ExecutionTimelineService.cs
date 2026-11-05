using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;

namespace Athena.UI.Services.ExecutionTimeline;

/// <summary>
/// Optional, metadata-only timeline storage. Recording and storage failures never fail a model
/// request or tool call. No automatic deletion: forked conversations can share the same bubble ID.
/// </summary>
public sealed class ExecutionTimelineService : IDisposable
{
    public const int MaxTraceBytes = 2 * 1024 * 1024;
    public const long MaxStoreBytes = 256L * 1024 * 1024;
    private const int RecentLimit = 32;
    private const int FailedNoticeLimit = 4096;
    private readonly IConfigService _config;
    private readonly ILogger _logger;
    private readonly string _directory;
    private readonly string _appDataRoot;
    private readonly int _maxTraceBytes;
    private readonly long _maxStoreBytes;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, ExecutionTimelineCollector> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExecutionTimelineSnapshot> _recent = new(StringComparer.Ordinal);
    private readonly Queue<string> _recentOrder = new();
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private readonly Queue<string> _failedOrder = new();
    private readonly object _recentGate = new();
    private AppConfig _observed;
    private bool _disposed;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public ExecutionTimelineService(IConfigService config, IPlatformPathService paths, ILogger logger,
        int maxTraceBytes = MaxTraceBytes, long maxStoreBytes = MaxStoreBytes)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        ArgumentNullException.ThrowIfNull(paths);
        _logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<ExecutionTimelineService>();
        if (maxTraceBytes <= 0 || maxStoreBytes < maxTraceBytes) throw new ArgumentOutOfRangeException(nameof(maxTraceBytes));
        _maxTraceBytes = maxTraceBytes;
        _maxStoreBytes = maxStoreBytes;
        _appDataRoot = Path.GetFullPath(paths.GetAppDataDirectory());
        _directory = Path.Combine(_appDataRoot, "ExecutionTimelines");
        _observed = _config.Load();
        _observed.PropertyChanged += OnPropertyChanged;
        _config.ConfigChanged += OnConfigChanged;
    }

    public bool Enabled => _config.Load().ExecutionAnalysisEnabled;
    public event EventHandler? EnabledChanged;

    public ExecutionTimelineCollector? TryBegin(string messageId, long startedAt, DateTimeOffset startedAtUtc)
    {
        if (!Enabled) return null;
        if (!Guid.TryParseExact(messageId, "N", out var parsedId) || parsedId.ToString("N") != messageId)
            throw new ArgumentException("A host-created assistant message ID is required.", nameof(messageId));
        var collector = new ExecutionTimelineCollector(messageId, startedAt, startedAtUtc);
        if (!_active.TryAdd(messageId, collector))
            throw new InvalidOperationException("A timeline for this assistant message is already active.");
        // The user could turn the switch off between the first check and adding the recorder.
        if (!Enabled) collector.StopCollecting();
        return collector;
    }

    public async Task CompleteAsync(ExecutionTimelineCollector collector, ExecutionTimelineState state, long completedAt = 0)
    {
        var snapshot = collector.Complete(state, completedAt);
        Remember(snapshot);
        try
        {
            await SaveAsync(snapshot).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            collector.MarkPersistenceFailed();
            Remember(collector.Snapshot());
            lock (_recentGate)
            {
                if (_failed.Add(snapshot.MessageId)) _failedOrder.Enqueue(snapshot.MessageId);
                while (_failedOrder.Count > FailedNoticeLimit) _failed.Remove(_failedOrder.Dequeue());
            }
            _logger.Warning(ex, "Execution timeline for message {MessageId} could not be persisted; the reply is unaffected", snapshot.MessageId);
        }
        finally { _active.TryRemove(snapshot.MessageId, out _); }
    }

    public async Task<ExecutionTimelineLoadResult> LoadAsync(string messageId, CancellationToken cancellationToken = default)
    {
        if (_active.TryGetValue(messageId, out var active))
        {
            var snapshot = active.Snapshot();
            // Keep the dialog refreshing until the async save succeeds or fails; otherwise a
            // window opened at the exact completion edge can miss a persistence failure forever.
            return new(snapshot, true);
        }
        lock (_recentGate)
        {
            if (_recent.TryGetValue(messageId, out var recent)) return new(recent, false);
            if (_failed.Contains(messageId)) return new(null, false, "storage_error");
        }
        if (!Guid.TryParseExact(messageId, "N", out var id)) return new(null, false);
        var path = Path.Combine(_directory, id.ToString("N") + ".json");
        try
        {
            EnsureInsideAppData(path);
            if (!File.Exists(path)) return new(null, false);
            if (new FileInfo(path).Length > _maxTraceBytes) throw new InvalidDataException("Execution timeline exceeds its read limit.");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous);
            var snapshot = await JsonSerializer.DeserializeAsync<ExecutionTimelineSnapshot>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (snapshot is null || snapshot.Version != 1 || snapshot.MessageId != id.ToString("N") || snapshot.Spans.Count > ExecutionTimelineCollector.MaxSpans)
                throw new InvalidDataException("Execution timeline identity, version or span count is invalid.");
            return new(snapshot, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Execution timeline could not be read for message {MessageId}", messageId);
            return new(null, false, "storage_error");
        }
    }

    private async Task SaveAsync(ExecutionTimelineSnapshot snapshot)
    {
        // Bounded diagnostics serialization may be sizeable. Move CPU work off the UI thread;
        // the operation is still awaited, and the actual write is asynchronous and atomic.
        var bytes = await Task.Run(() => JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions)).ConfigureAwait(false);
        if (bytes.Length > _maxTraceBytes) throw new IOException("Execution timeline exceeds its storage limit.");
        await _writeGate.WaitAsync().ConfigureAwait(false);
        string? temporary = null;
        try
        {
            EnsureInsideAppData(_directory);
            Directory.CreateDirectory(_directory);
            EnsureInsideAppData(_directory);
            var current = Directory.EnumerateFiles(_directory, "*.json").Sum(path => new FileInfo(path).Length);
            if (current + bytes.Length > _maxStoreBytes) throw new IOException("Execution timeline store is full; existing records were not removed.");
            temporary = Path.Combine(_directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Share = FileShare.None, BufferSize = 16 * 1024, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(bytes).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            File.Move(temporary, Path.Combine(_directory, snapshot.MessageId + ".json"), overwrite: false);
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (IOException ex) { _logger.Warning(ex, "Could not remove incomplete execution timeline temporary file"); }
                catch (UnauthorizedAccessException ex) { _logger.Warning(ex, "Could not remove incomplete execution timeline temporary file"); }
            }
            _writeGate.Release();
        }
    }

    private void EnsureInsideAppData(string path)
    {
        if (!FileSystemService.TryResolveFully(_appDataRoot, out var root)
            || !FileSystemService.TryResolveFully(path, out var resolved))
            throw new UnauthorizedAccessException("Execution timeline path could not be resolved safely.");
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Execution timeline path escapes the application data directory.");
    }

    private void Remember(ExecutionTimelineSnapshot snapshot)
    {
        lock (_recentGate)
        {
            if (!_recent.ContainsKey(snapshot.MessageId)) _recentOrder.Enqueue(snapshot.MessageId);
            _recent[snapshot.MessageId] = snapshot;
            while (_recentOrder.Count > RecentLimit) _recent.Remove(_recentOrder.Dequeue());
        }
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.PropertyName != nameof(AppConfig.ExecutionAnalysisEnabled)) return;
        if (!_observed.ExecutionAnalysisEnabled)
            foreach (var collector in _active.Values) collector.StopCollecting();
        if (EnabledChanged is { } handlers)
            foreach (EventHandler handler in handlers.GetInvocationList())
                try { handler(this, EventArgs.Empty); }
                catch (Exception ex) { _logger.Warning(ex, "Execution analysis setting observer failed"); }
    }

    private void OnConfigChanged(object? sender, AppConfig config)
    {
        if (_disposed) return;
        if (ReferenceEquals(config, _observed)) return;
        _observed.PropertyChanged -= OnPropertyChanged;
        _observed = config;
        _observed.PropertyChanged += OnPropertyChanged;
        OnPropertyChanged(config, new PropertyChangedEventArgs(nameof(AppConfig.ExecutionAnalysisEnabled)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _observed.PropertyChanged -= OnPropertyChanged;
        _config.ConfigChanged -= OnConfigChanged;
        _writeGate.Dispose();
    }
}
