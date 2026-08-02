using Porta.Pty;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services;

public sealed class TerminalOutputEventArgs(byte[] data) : EventArgs
{
    public byte[] Data { get; } = data;
}

public sealed class TerminalSession : IAsyncDisposable
{
    private const int InitialColumns = 100;
    private const int InitialRows = 24;
    private static readonly TimeSpan ReaderShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly IPtyConnection _connection;
    private readonly CancellationTokenSource _readCancellation = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ILogger _logger;
    private readonly Task _readTask;
    private bool _disposed;
    private bool _exitRaised;

    private TerminalSession(
        string id,
        string scopeKey,
        string name,
        string shellName,
        string workingDirectory,
        IPtyConnection connection,
        ILogger logger)
    {
        Id = id;
        ScopeKey = scopeKey;
        Name = name;
        ShellName = shellName;
        WorkingDirectory = workingDirectory;
        _connection = connection;
        _logger = logger.ForContext<TerminalSession>()
            .ForContext("TerminalId", id)
            .ForContext("TerminalScope", scopeKey);
        _connection.ProcessExited += OnProcessExited;
        _readTask = ReadOutputAsync(_readCancellation.Token);
    }

    public event EventHandler<TerminalOutputEventArgs>? OutputReceived;

    public event EventHandler? Exited;

    public string Id { get; }

    public string ScopeKey { get; }

    public string Name { get; }

    public string ShellName { get; }

    public string WorkingDirectory { get; }

    public int ProcessId => _connection.Pid;

    public bool IsRunning { get; private set; } = true;

    public static async Task<TerminalSession> StartAsync(
        string scopeKey,
        string name,
        string workingDirectory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var shell = ResolveShell();
        var options = new PtyOptions
        {
            Name = name,
            Cols = InitialColumns,
            Rows = InitialRows,
            Cwd = workingDirectory,
            App = shell.Executable,
            CommandLine = shell.Arguments,
            Environment = new Dictionary<string, string>
            {
                ["TERM"] = "xterm-256color",
                ["COLORTERM"] = "truecolor"
            }
        };

        var connection = await PtyProvider.SpawnAsync(options, cancellationToken);
        return new TerminalSession(
            Guid.NewGuid().ToString("N"),
            scopeKey,
            name,
            shell.DisplayName,
            workingDirectory,
            connection,
            logger);
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (_disposed || !IsRunning || data.IsEmpty) return;

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _connection.WriterStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await _connection.WriterStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _logger.Debug(ex, "Terminal input stream closed");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Resize(int columns, int rows)
    {
        if (_disposed || !IsRunning) return;
        try
        {
            _connection.Resize(Math.Clamp(columns, 2, 500), Math.Clamp(rows, 1, 200));
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Terminal resize failed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _connection.ProcessExited -= OnProcessExited;
        _readCancellation.Cancel();

        if (IsRunning)
        {
            try
            {
                _connection.Kill();
                _connection.WaitForExit(1500);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Terminal process termination failed");
            }
        }

        IsRunning = false;
        try
        {
            _connection.ReaderStream.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Terminal output stream disposal failed");
        }
        _connection.Dispose();

        var readerCompleted = await Task.WhenAny(
                _readTask,
                Task.Delay(ReaderShutdownTimeout))
            .ConfigureAwait(false) == _readTask;
        if (readerCompleted)
        {
            try
            {
                await _readTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            _readCancellation.Dispose();
        }
        else
        {
            // Porta.Pty 1.0.7 can leave a native macOS read pending after both
            // cancellation and connection disposal. The process and native
            // connection are already closed, so never let UI shutdown wait
            // without a bound; release the CTS if the pending read later exits.
            _logger.Warning(
                "Terminal output reader did not stop within {TimeoutMs} ms after connection disposal: {TerminalName}",
                ReaderShutdownTimeout.TotalMilliseconds,
                Name);
            _ = DisposeReadCancellationWhenReaderCompletesAsync();
        }

        _writeGate.Dispose();
    }

    private async Task DisposeReadCancellationWhenReaderCompletesAsync()
    {
        try
        {
            await _readTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _readCancellation.Dispose();
        }
    }

    private async Task ReadOutputAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = await _connection.ReaderStream.ReadAsync(buffer, cancellationToken);
                if (count <= 0) break;
                var copy = new byte[count];
                Buffer.BlockCopy(buffer, 0, copy, 0, count);
                OutputReceived?.Invoke(this, new TerminalOutputEventArgs(copy));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _logger.Debug(ex, "Terminal output stream closed");
        }
    }

    private void OnProcessExited(object? sender, PtyExitedEventArgs e)
    {
        IsRunning = false;
        _logger.Information(
            "Terminal exited: {TerminalName}, PID={ProcessId}, ExitCode={ExitCode}",
            Name,
            ProcessId,
            e.ExitCode);
        RaiseExitedOnce();
    }

    private void RaiseExitedOnce()
    {
        if (_exitRaised) return;
        _exitRaised = true;
        Exited?.Invoke(this, EventArgs.Empty);
    }

    private static ShellLaunchInfo ResolveShell()
    {
        if (OperatingSystem.IsWindows())
        {
            var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var windowsPowerShell = Path.Combine(
                systemDirectory,
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            return new ShellLaunchInfo(
                File.Exists(windowsPowerShell) ? windowsPowerShell : "powershell.exe",
                [],
                "PS");
        }

        var configuredShell = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrWhiteSpace(configuredShell) && File.Exists(configuredShell))
        {
            return new ShellLaunchInfo(
                configuredShell,
                ["-l"],
                Path.GetFileName(configuredShell));
        }

        if (OperatingSystem.IsMacOS())
            return new ShellLaunchInfo("/bin/zsh", ["-l"], "zsh");

        return new ShellLaunchInfo("/bin/bash", ["-l"], "bash");
    }

    private sealed record ShellLaunchInfo(
        string Executable,
        string[] Arguments,
        string DisplayName);
}
