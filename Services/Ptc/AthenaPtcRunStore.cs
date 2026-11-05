using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Services.Interfaces;
using Ptc.Core;
using Serilog;

namespace Athena.UI.Services.Ptc;

public sealed record AthenaPtcStoredRun(int FormatVersion, DateTimeOffset CreatedAtUtc, string Code, PtcExecutionResult Execution);

/// <summary>Immutable, atomic host-owned traces. No deletion, replay, or caller-selected paths.</summary>
public sealed class AthenaPtcRunStore : IDisposable
{
    public const int DefaultMaxRunBytes = 16 * 1024 * 1024;
    public const long DefaultMaxStoreBytes = 256L * 1024 * 1024;
    private readonly string _directory;
    private readonly ILogger _logger;
    private readonly int _maxRunBytes;
    private readonly long _maxStoreBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _reservations = new(StringComparer.Ordinal);
    private readonly object _reservationGate = new();

    public AthenaPtcRunStore(IPlatformPathService paths, ILogger logger,
        int maxRunBytes = DefaultMaxRunBytes, long maxStoreBytes = DefaultMaxStoreBytes)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        if (maxRunBytes <= 0 || maxStoreBytes < maxRunBytes) throw new ArgumentOutOfRangeException(nameof(maxRunBytes));
        _directory = Path.Combine(paths.GetAppDataDirectory(), "PtcRuns");
        _logger = logger.ForContext<AthenaPtcRunStore>();
        _maxRunBytes = maxRunBytes;
        _maxStoreBytes = maxStoreBytes;
    }

    public async Task<Reservation> ReserveAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            var bytes = Directory.EnumerateFiles(_directory).Sum(path => new FileInfo(path).Length);
            var id = Guid.NewGuid().ToString("N");
            lock (_reservationGate)
            {
                if (bytes + ((long)_reservations.Count + 1) * _maxRunBytes > _maxStoreBytes)
                    throw new PtcRuntimeException("trace_store_full", "PTC trace storage is full. Back up PtcRuns through the host before enabling more executions.", PtcRunStatus.Rejected);
                _reservations.Add(id);
            }
            return new Reservation(this, id);
        }
        finally { _gate.Release(); }
    }

    public async Task<string> SaveAsync(Reservation reservation, string code, PtcExecutionResult execution, CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(reservation.Owner, this) || execution.Context.RunId != reservation.RunId)
            throw new ArgumentException("Trace reservation does not match the run.");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new AthenaPtcStoredRun(1, DateTimeOffset.UtcNow, code, execution), PtcJson.Options);
        if (payload.Length > _maxRunBytes)
            throw new PtcRuntimeException("trace_store_limit_exceeded", "Run record exceeds its storage limit.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporary = Path.Combine(_directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            lock (_reservationGate)
                if (!_reservations.Contains(reservation.RunId)) throw new InvalidOperationException("Trace reservation is closed.");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, Path.Combine(_directory, reservation.RunId + ".json"), overwrite: false);
            return "ptc:" + reservation.RunId;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "PTC run trace could not be committed for {RunId}", reservation.RunId);
            throw;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (IOException ex) { _logger.Warning(ex, "Could not remove incomplete PTC trace {Temporary}", temporary); }
                catch (UnauthorizedAccessException ex) { _logger.Warning(ex, "Could not remove incomplete PTC trace {Temporary}", temporary); }
            }
            _gate.Release();
        }
    }

    public async Task<AthenaPtcStoredRun?> ReadAsync(string traceRef, CancellationToken cancellationToken = default)
    {
        if (!traceRef.StartsWith("ptc:", StringComparison.Ordinal) || !Guid.TryParseExact(traceRef[4..], "N", out var id))
            throw new ArgumentException("Not a host-created PTC trace reference.", nameof(traceRef));
        var path = Path.Combine(_directory, id.ToString("N") + ".json");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > _maxRunBytes) throw new InvalidDataException("PTC record exceeds the read limit.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        var record = await JsonSerializer.DeserializeAsync<AthenaPtcStoredRun>(stream, PtcJson.Options, cancellationToken).ConfigureAwait(false);
        if (record is null || record.FormatVersion != 1 || record.Execution.Context.RunId != id.ToString("N"))
            throw new InvalidDataException("PTC record identity or version does not match.");
        return record;
    }

    public void Dispose() => _gate.Dispose();

    public sealed class Reservation(AthenaPtcRunStore owner, string runId) : IDisposable
    {
        internal AthenaPtcRunStore Owner { get; } = owner;
        public string RunId { get; } = runId;
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // Pure memory accounting: no I/O, dispatcher wait, unbounded async cleanup or fire-and-forget task.
            lock (Owner._reservationGate) Owner._reservations.Remove(RunId);
        }
    }
}
