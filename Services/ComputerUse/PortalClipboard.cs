using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Serilog;
using Tmds.DBus.Protocol;

namespace Athena.UI.Services.ComputerUse;

// Uses the RemoteDesktop session's Clipboard portal so pasted UTF-8 text bypasses active IME
// composition and reaches both Wayland and XWayland clients that obey compositor focus.
internal sealed class PortalClipboard : IDisposable
{
    internal const string Interface = "org.freedesktop.portal.Clipboard";
    private const string DefaultService = "org.freedesktop.portal.Desktop";
    private const string DefaultPath = "/org/freedesktop/portal/desktop";
    private const int MaxSavedClipboardBytes = 256 * 1024;
    private const int TransferTimeoutMs = 500;
    private const int PostTransferGraceMs = 80;
    private const int ReadSavedTimeoutMs = 350;
    internal static readonly string[] TextMimeTypes = ["text/plain;charset=utf-8", "UTF8_STRING", "text/plain"];

    private readonly DBusConnection _bus;
    private readonly string _session;
    private readonly Action<CancellationToken> _checkOpen;
    private readonly string _service;
    private readonly string _desktopPath;
    private readonly object _gate = new();
    private IDisposable? _transferWatch;
    private IDisposable? _ownerWatch;
    private byte[]? _advertisedBytes;
    private long _selectionGeneration;
    private long _servedGeneration;
    private TaskCompletionSource<bool>? _transferCompleted;
    private bool _sessionIsOwner;
    private bool _lostOwnershipDuringPaste;
    private string? _externalTextMimeType;
    private volatile bool _disposed;

    private PortalClipboard(DBusConnection bus, string session, Action<CancellationToken> checkOpen,
        string service, string desktopPath)
    {
        _bus = bus;
        _session = session;
        _checkOpen = checkOpen;
        _service = service;
        _desktopPath = desktopPath;
    }

    internal static async Task<bool> RequestAsync(DBusConnection bus, string session, CancellationToken token,
        string service = DefaultService, string desktopPath = DefaultPath)
    {
        var writer = bus.GetMessageWriter();
        MessageBuffer message;
        try
        {
            writer.WriteMethodCallHeader(service, desktopPath, Interface, "RequestClipboard", "oa{sv}");
            writer.WriteObjectPath(session);
            writer.WriteDictionary(new Dictionary<string, VariantValue>());
            message = writer.CreateMessage();
        }
        finally { writer.Dispose(); }

        try
        {
            await bus.CallMethodAsync(message).WaitAsync(token);
            return true;
        }
        catch (DBusErrorReplyException)
        {
            return false;
        }
    }

    internal static async Task<PortalClipboard> AttachAsync(DBusConnection bus, string session,
        Action<CancellationToken> checkOpen, CancellationToken token,
        string service = DefaultService, string desktopPath = DefaultPath)
    {
        var clipboard = new PortalClipboard(bus, session, checkOpen, service, desktopPath);
        try
        {
            await clipboard.SubscribeAsync(token);
            return clipboard;
        }
        catch
        {
            clipboard.Dispose();
            throw;
        }
    }

    private async Task SubscribeAsync(CancellationToken token)
    {
        _transferWatch = await _bus.WatchSignalAsync(_service, _desktopPath, Interface, "SelectionTransfer",
            static (m, _) =>
            {
                var r = m.GetBodyReader();
                return (Session: r.ReadObjectPathAsString(), MimeType: r.ReadString(), Serial: r.ReadUInt32());
            },
            n =>
            {
                if (!n.HasValue || _disposed || n.Value.Session != _session) return;
                var (mimeType, serial) = (n.Value.MimeType, n.Value.Serial);
                _ = Task.Run(() => HandleTransferAsync(mimeType, serial));
            },
            flags: ObserverFlags.None, emitOnCapturedContext: false).AsTask().WaitAsync(token);

        _ownerWatch = await _bus.WatchSignalAsync(_service, _desktopPath, Interface, "SelectionOwnerChanged",
            static (m, _) =>
            {
                var r = m.GetBodyReader();
                return (Session: r.ReadObjectPathAsString(), Options: r.ReadDictionaryOfStringToVariantValue());
            },
            n =>
            {
                if (!n.HasValue || _disposed || n.Value.Session != _session) return;
                OnOwnerChanged(n.Value.Options);
            },
            flags: ObserverFlags.None, emitOnCapturedContext: false).AsTask().WaitAsync(token);
    }

    private void OnOwnerChanged(Dictionary<string, VariantValue> options)
    {
        bool isOwner = options.TryGetValue("session_is_owner", out var own)
            && own.Type == VariantValueType.Bool && own.GetBool();
        string? textMime = null;
        if (!isOwner && options.TryGetValue("mime_types", out var mimes))
            textMime = SelectPreferredTextMime(mimes);

        lock (_gate)
        {
            bool wasOwner = _sessionIsOwner;
            _sessionIsOwner = isOwner;
            if (!isOwner)
            {
                _externalTextMimeType = textMime;
                if (wasOwner)
                {
                    _lostOwnershipDuringPaste = true;
                    _advertisedBytes = null;
                }
            }
        }
    }

    internal static string? SelectPreferredTextMime(VariantValue mimes)
    {
        if (mimes.Type == VariantValueType.Struct && mimes.Count == 1)
            mimes = mimes.GetItem(0);
        if (mimes.Type != VariantValueType.Array) return null;
        var offered = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < mimes.Count; i++)
        {
            var item = mimes.GetItem(i);
            if (item.Type == VariantValueType.String) offered.Add(item.GetString());
        }
        foreach (var candidate in TextMimeTypes)
            if (offered.Contains(candidate)) return candidate;
        return null;
    }

    internal static bool IsSupportedTextMime(string mimeType) =>
        mimeType is "text/plain;charset=utf-8" or "UTF8_STRING" or "text/plain"
        || mimeType.StartsWith("text/plain;", StringComparison.OrdinalIgnoreCase);

    internal async Task<bool> PasteTextAsync(string text, Func<CancellationToken, Task> sendPasteShortcut,
        CancellationToken token)
    {
        _checkOpen(token);
        string? savedMime;
        lock (_gate)
            savedMime = !_sessionIsOwner ? _externalTextMimeType : null;

        byte[]? savedBytes = savedMime is not null ? await TryReadExternalAsync(savedMime, token) : null;
        var payload = Encoding.UTF8.GetBytes(text);
        long pasteGeneration;
        lock (_gate)
        {
            _selectionGeneration++;
            pasteGeneration = _selectionGeneration;
            _advertisedBytes = payload;
            _transferCompleted = null;
            _lostOwnershipDuringPaste = false;
        }

        await SetSelectionAsync(token);
        await Task.Delay(35, token);

        var transferTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool hijackedAfterRead;
        lock (_gate)
        {
            hijackedAfterRead = _lostOwnershipDuringPaste && _servedGeneration == pasteGeneration;
            if (!hijackedAfterRead)
                _transferCompleted = transferTcs;
        }

        await sendPasteShortcut(token);

        bool transferred = hijackedAfterRead;
        if (!transferred)
        {
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                wait.CancelAfter(TransferTimeoutMs);
                transferred = await transferTcs.Task.WaitAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                transferred = false;
            }
        }

        if (transferred)
            await Task.Delay(PostTransferGraceMs, token);

        if (savedBytes is not null)
        {
            bool stillOurs;
            lock (_gate)
            {
                stillOurs = !_disposed && !_lostOwnershipDuringPaste;
                if (stillOurs)
                {
                    _selectionGeneration++;
                    _advertisedBytes = savedBytes;
                    _transferCompleted = null;
                }
            }
            if (stillOurs)
            {
                try { await SetSelectionAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None); }
                catch (Exception e) { Log.Debug(e, "Could not restore previous desktop clipboard text"); }
            }
        }
        else
        {
            lock (_gate)
            {
                if (ReferenceEquals(_transferCompleted, transferTcs)) _transferCompleted = null;
            }
        }

        return transferred;
    }

    private Task SetSelectionAsync(CancellationToken token)
    {
        _checkOpen(token);
        var writer = _bus.GetMessageWriter();
        MessageBuffer message;
        try
        {
            writer.WriteMethodCallHeader(_service, _desktopPath, Interface, "SetSelection", "oa{sv}");
            writer.WriteObjectPath(_session);
            writer.WriteDictionary(new Dictionary<string, VariantValue>
            {
                ["mime_types"] = VariantValue.Array(TextMimeTypes)
            });
            message = writer.CreateMessage();
        }
        finally { writer.Dispose(); }
        return _bus.CallMethodAsync(message).WaitAsync(token);
    }

    private async Task<byte[]?> TryReadExternalAsync(string mimeType, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(ReadSavedTimeoutMs);
        SafeFileHandle? fd = null;
        try
        {
            var writer = _bus.GetMessageWriter();
            MessageBuffer message;
            try
            {
                writer.WriteMethodCallHeader(_service, _desktopPath, Interface, "SelectionRead", "os");
                writer.WriteObjectPath(_session);
                writer.WriteString(mimeType);
                message = writer.CreateMessage();
            }
            finally { writer.Dispose(); }

            fd = await _bus.CallMethodAsync(message, static (m, _) => m.GetBodyReader().ReadHandle<SafeFileHandle>())
                .WaitAsync(limit.Token);
            if (fd.IsInvalid) return null;

            using var closeOnCancel = limit.Token.Register(() => { try { fd.Dispose(); } catch { } });
            await using var stream = new FileStream(fd, FileAccess.Read, bufferSize: 4096, isAsync: false);
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int total = 0;
            while (true)
            {
                int read = await stream.ReadAsync(chunk, limit.Token);
                if (read == 0) break;
                total += read;
                if (total > MaxSavedClipboardBytes) return null;
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (Exception e) when (!token.IsCancellationRequested &&
            e is DBusErrorReplyException or DBusConnectionException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            return null;
        }
        finally
        {
            fd?.Dispose();
        }
    }

    private async Task HandleTransferAsync(string mimeType, uint serial)
    {
        byte[]? payload;
        long generation;
        TaskCompletionSource<bool>? tcs;
        lock (_gate)
        {
            payload = _advertisedBytes;
            generation = _selectionGeneration;
            tcs = _transferCompleted;
        }

        if (_disposed || payload is null || !IsSupportedTextMime(mimeType))
        {
            await SendWriteDoneAsync(serial, success: false);
            return;
        }

        bool success = false;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        SafeFileHandle? fd = null;
        try
        {
            var writer = _bus.GetMessageWriter();
            MessageBuffer message;
            try
            {
                writer.WriteMethodCallHeader(_service, _desktopPath, Interface, "SelectionWrite", "ou");
                writer.WriteObjectPath(_session);
                writer.WriteUInt32(serial);
                message = writer.CreateMessage();
            }
            finally { writer.Dispose(); }

            fd = await _bus.CallMethodAsync(message, static (m, _) => m.GetBodyReader().ReadHandle<SafeFileHandle>())
                .WaitAsync(limit.Token);
            if (!fd.IsInvalid)
            {
                using (var stream = new FileStream(fd, FileAccess.Write, bufferSize: 4096, isAsync: false))
                {
                    await stream.WriteAsync(payload, limit.Token);
                    await stream.FlushAsync(limit.Token);
                }
                fd = null; // Owned and closed by FileStream so the reader sees EOF before SelectionWriteDone.
                success = true;
            }
        }
        catch (Exception e)
        {
            Log.Debug(e, "Desktop portal clipboard transfer failed for {MimeType}", mimeType);
        }
        finally
        {
            fd?.Dispose();
            await SendWriteDoneAsync(serial, success);
            if (success)
            {
                lock (_gate) _servedGeneration = generation;
                tcs?.TrySetResult(true);
            }
        }
    }

    private async Task SendWriteDoneAsync(uint serial, bool success)
    {
        if (_disposed) return;
        try
        {
            var writer = _bus.GetMessageWriter();
            MessageBuffer message;
            try
            {
                writer.WriteMethodCallHeader(_service, _desktopPath, Interface, "SelectionWriteDone", "oub");
                writer.WriteObjectPath(_session);
                writer.WriteUInt32(serial);
                writer.WriteBool(success);
                message = writer.CreateMessage();
            }
            finally { writer.Dispose(); }
            await _bus.CallMethodAsync(message).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception e)
        {
            Log.Debug(e, "Desktop portal clipboard write completion ended");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            _advertisedBytes = null;
            _transferCompleted?.TrySetResult(false);
            _transferCompleted = null;
        }
        _transferWatch?.Dispose();
        _ownerWatch?.Dispose();
    }
}
