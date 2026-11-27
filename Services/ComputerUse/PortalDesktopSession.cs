using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Serilog;
using Tmds.DBus.Protocol;

namespace Athena.UI.Services.ComputerUse;

// One user-approved monitor and its keyboard/pointer share a single portal session.
internal sealed class PortalDesktopSession(string address) : IComputerUseSession
{
    private readonly DBusConnection bus = new(address);
    private const string Service = "org.freedesktop.portal.Desktop", Desktop = "/org/freedesktop/portal/desktop";
    private const string Remote = "org.freedesktop.portal.RemoteDesktop", Cast = "org.freedesktop.portal.ScreenCast";
    private const string Session = "org.freedesktop.portal.Session";
    private static readonly Dictionary<string, VariantValue> Empty = [];
    private delegate void Body(ref MessageWriter writer);
    private string _session = "";
    private uint _stream;
    private int _width, _height, _streamWidth, _streamHeight;
    private Avalonia.PixelRect? _monitorBounds;
    private bool _inputUsesStreamPixels;
    private SafeFileHandle? _remote;
    private PortalVideoStream? _video;
    private PortalClipboard? _clipboard;
    private IDisposable? _closedWatch;
    private volatile bool _closed;
    private ComputerUseFrame? _latest;
    private bool _firstObservation = true;

    internal static bool IsWayland => OperatingSystem.IsLinux() &&
        (Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") is { Length: > 0 } type
            ? type.Equals("wayland", StringComparison.OrdinalIgnoreCase)
            : !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")));

    private static bool IsGnome => Array.Exists((Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Split(':'),
        name => name.Equals("GNOME", StringComparison.OrdinalIgnoreCase));

    internal static async Task<PortalDesktopSession> OpenAsync(CancellationToken token)
    {
        var address = DBusAddress.Session ?? throw new InvalidOperationException("No desktop D-Bus session is available.");
        var session = new PortalDesktopSession(address);
        try
        {
            await session.InitializeAsync(token);
            return session;
        }
        catch
        {
            await session.EndAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task InitializeAsync(CancellationToken token)
    {
        await bus.ConnectAsync().AsTask().WaitAsync(token);
        // Register the same desktop-entry ID for launcher, source and subsequent process starts.
        try
        {
            await bus.CallMethodAsync(Message("org.freedesktop.host.portal.Registry", "Register", "sa{sv}",
                (ref MessageWriter w) => { w.WriteString("athena"); w.WriteDictionary(Empty); })).WaitAsync(token);
        }
        catch (DBusErrorReplyException) { /* Older portals or packaged apps use their system-assigned identity. */ }
        var version = await bus.CallMethodAsync(Message("org.freedesktop.DBus.Properties", "Get", "ss",
            (ref MessageWriter w) => { w.WriteString(Remote); w.WriteString("version"); }),
            static (m, _) => m.GetBodyReader().ReadVariantValue().GetUInt32()).WaitAsync(token);
        var restore = version >= 2 ? PortalRestoreToken.ForUser() : null;
        var sessionToken = "athena_" + Guid.NewGuid().ToString("N");
        var created = await RequestAsync(Remote, "CreateSession", "a{sv}", null,
            new() { ["session_handle_token"] = sessionToken }, token);
        var handle = created["session_handle"];
        _session = handle.Type == VariantValueType.String ? handle.GetString() : handle.GetObjectPathAsString();
        _closedWatch = await bus.WatchSignalAsync(Service, _session, Session, "Closed", static (_, _) => true,
            _ => _closed = true, flags: ObserverFlags.EmitOnConnectionClosed, emitOnCapturedContext: false);
        var options = new Dictionary<string, VariantValue> { ["types"] = 3u };
        if (restore is not null)
        {
            options["persist_mode"] = 2u;
            if (restore.Take() is { } previous) options["restore_token"] = previous;
        }
        await RequestAsync(Remote, "SelectDevices", "oa{sv}", WriteSession, options, token);
        await RequestAsync(Cast, "SelectSources", "oa{sv}", WriteSession,
            new() { ["types"] = 1u, ["multiple"] = false, ["cursor_mode"] = 1u }, token);
        bool clipboardRequested = version >= 2 && await PortalClipboard.RequestAsync(bus, _session, token);
        if (clipboardRequested)
            _clipboard = await PortalClipboard.AttachAsync(bus, _session, CheckOpen, token);
        var started = await RequestAsync(Remote, "Start", "osa{sv}",
            (ref MessageWriter w) => { WriteSession(ref w); w.WriteString(""); }, [], token);
        if (started.TryGetValue("restore_token", out var replacement)) restore?.Save(replacement.GetString());
        if (!started.TryGetValue("devices", out var devices) || (devices.GetUInt32() & 3) != 3 ||
            !started.TryGetValue("streams", out var streams) || streams.Count != 1)
            throw new InvalidOperationException("Desktop control needs one shared monitor, keyboard and pointer.");
        bool clipboardEnabled = clipboardRequested
            && started.TryGetValue("clipboard_enabled", out var enabled)
            && enabled.Type == VariantValueType.Bool && enabled.GetBool();
        if (!clipboardEnabled && _clipboard is not null)
        {
            _clipboard.Dispose();
            _clipboard = null;
            Log.Debug("Desktop portal did not enable clipboard access for session {Session}", _session);
        }
        var stream = streams.GetItem(0);
        _stream = stream.GetItem(0).GetUInt32();
        var info = stream.GetItem(1).GetDictionary<string, VariantValue>();
        bool explicitLogicalSize = info.TryGetValue("logical_size", out var size);
        if (!explicitLogicalSize && !info.TryGetValue("size", out size))
            throw new InvalidOperationException("The portal did not provide the monitor's logical input dimensions.");
        // GNOME's legacy 'size' is the monitor layout. Mutter divides Notify coordinates
        // by the monitor scale itself, so this backend needs the original PipeWire pixel size.
        _inputUsesStreamPixels = !explicitLogicalSize && IsGnome;
        _width = size.GetItem(0).GetInt32();
        _height = size.GetItem(1).GetInt32();
        if (_width <= 0 || _height <= 0) throw new InvalidOperationException("Invalid shared monitor dimensions.");
        if (info.TryGetValue("position", out var position))
            _monitorBounds = new(position.GetItem(0).GetInt32(), position.GetItem(1).GetInt32(), _width, _height);
        var message = Message(Cast, "OpenPipeWireRemote", "oa{sv}", (ref MessageWriter w) =>
        { WriteSession(ref w); w.WriteDictionary(Empty); });
        // Keep ownership until the pipeline shuts down. pipewiresrc duplicates this descriptor.
        _remote = await bus.CallMethodAsync(message, static (m, _) => m.GetBodyReader().ReadHandle<SafeFileHandle>());
        token.ThrowIfCancellationRequested();
        _video = new PortalVideoStream(_remote, _stream, _width, _height);
    }

    private void WriteSession(ref MessageWriter writer) => writer.WriteObjectPath(_session);

    private MessageBuffer Message(string iface, string method, string? signature, Body? body = null, string path = Desktop)
    {
        var writer = bus.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(Service, path, iface, method, signature);
            body?.Invoke(ref writer);
            return writer.CreateMessage();
        }
        finally { writer.Dispose(); }
    }

    private async Task<Dictionary<string, VariantValue>> RequestAsync(string iface, string method, string sig,
        Body? prefix, Dictionary<string, VariantValue> options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var handle = "athena_" + Guid.NewGuid().ToString("N");
        var path = $"{Desktop}/request/{bus.UniqueName![1..].Replace('.', '_')}/{handle}";
        var reply = new TaskCompletionSource<(uint Code, Dictionary<string, VariantValue> Values)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = await bus.WatchSignalAsync(Service, path, "org.freedesktop.portal.Request", "Response",
            static (m, _) => { var r = m.GetBodyReader(); return (r.ReadUInt32(), r.ReadDictionaryOfStringToVariantValue()); },
            n => { if (n.HasValue) reply.TrySetResult(n.Value); else if (n.Exception is { } e) reply.TrySetException(e); },
            flags: ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnReaderFailed, emitOnCapturedContext: false);
        options["handle_token"] = handle;
        var request = Message(iface, method, sig, (ref MessageWriter w) => { prefix?.Invoke(ref w); w.WriteDictionary(options); });
        var actual = await bus.CallMethodAsync(request, static (m, _) => m.GetBodyReader().ReadObjectPathAsString()).WaitAsync(token);
        if (actual != path) throw new InvalidOperationException("Unexpected GNOME portal request handle.");
        try
        {
            var (code, values) = await reply.Task.WaitAsync(token);
            if (code != 0) throw new InvalidOperationException(code == 1 ? "Desktop sharing was declined or cancelled."
                : $"The desktop portal could not complete {method} (response {code}).");
            return values;
        }
        catch (OperationCanceledException)
        {
            // Dismissing the in-flight picker also works before a session handle exists.
            try { await bus.CallMethodAsync(Message("org.freedesktop.portal.Request", "Close", null, path: path))
                    .WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None); } catch (Exception) { }
            throw;
        }
    }

    public Task<ComputerUseFrame> ObserveAsync(CancellationToken token) => ObserveAsync(token, fresh: false);

    internal async Task<ComputerUseFrame> ObserveAsync(CancellationToken token, bool fresh)
    {
        CheckOpen(token);
        if (fresh) await _video!.WaitForNextBufferAsync(token);
        var frame = await _video!.ReadAsync(token);
        if (_firstObservation)
        {
            // GNOME can put the closing share dialog in the first PipeWire frame.
            await Task.Delay(450, token);
            frame = await _video.ReadAsync(token);
            _firstObservation = false;
        }
        CheckOpen(token);
        (_streamWidth, _streamHeight) = _inputUsesStreamPixels ? _video.GetSourceSize() : (_width, _height);
        return _latest = frame with { FromPortal = true, PortalMonitor = _monitorBounds };
    }

    private void CheckOpen(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_closed) throw new InvalidOperationException("Desktop sharing has ended.");
    }

    private Task Notify(string method, string sig, Body body, CancellationToken token)
    {
        CheckOpen(token);
        return bus.CallMethodAsync(Message(Remote, method, "oa{sv}" + sig, (ref MessageWriter w) =>
        { WriteSession(ref w); w.WriteDictionary(Empty); body(ref w); }));
    }

    public async Task<string> ActAsync(ComputerUseFrame frame, ComputerUseAction action, double x, double y, CancellationToken token)
    {
        CheckOpen(token);
        if (!ReferenceEquals(frame, _latest)) throw new InvalidOperationException("Observe the shared monitor again before sending input.");
        _latest = null;
        if (action.Action is "click" or "double_click" or "right_click")
        {
            int button = action.Action == "right_click" ? 0x111 : 0x110;
            await ClickPointerAsync(frame, x, y, button, action.Action == "double_click", token);
        }
        else if (action.Action == "scroll")
        {
            await MovePointerAsync(frame, x, y, token);
            await Task.Delay(25, token);
            var horizontal = action.Direction is "left" or "right";
            var steps = action.Direction is "up" or "left" ? -3 : 3;
            await Notify("NotifyPointerAxisDiscrete", "ui", (ref MessageWriter w) =>
            { w.WriteUInt32(horizontal ? 1u : 0u); w.WriteInt32(steps); }, token);
        }
        else if (action.Action == "type_text")
        {
            if (action.X is not null && action.Y is not null)
            {
                await ClickPointerAsync(frame, x, y, 0x110, doubleClick: false, token);
                await Task.Delay(140, token);
            }
            if (_clipboard is not null)
            {
                bool pasted = await _clipboard.PasteTextAsync(action.Text!,
                    ct => PressKeyChordAsync([0xffe3, 'v'], ct), token);
                return pasted
                    ? "Text pasted into the focused desktop control. Inspect the next screenshot."
                    : "Paste shortcut was sent, but no focused control read the clipboard. Click the target input field directly (or include x and y with type_text) and verify focus before retrying.";
            }
            foreach (var rune in action.Text!.EnumerateRunes())
            {
                var symbol = rune.Value switch { 10 or 13 => 0xff0d, 9 => 0xff09, <= 255 => rune.Value, _ => 0x01000000 | rune.Value };
                await Key(symbol, 1, token);
                await Task.Delay(10, CancellationToken.None);
                await Key(symbol, 0, CancellationToken.None);
                await Task.Delay(10, token);
            }
        }
        else if (action.Action == "press_key" && DesktopKeys.TryParse(action.Key, out var keys))
        {
            var symbols = new int[keys.Length];
            for (int i = 0; i < keys.Length; i++) symbols[i] = KeySym(keys[i]);
            await PressKeyChordAsync(symbols, token);
        }
        else throw new InvalidOperationException("Unsupported desktop action.");
        return "Input sent to the shared desktop. Inspect the next screenshot.";
    }

    private async Task ClickPointerAsync(ComputerUseFrame frame, double x, double y, int button, bool doubleClick, CancellationToken token)
    {
        await MovePointerAsync(frame, x, y, token);
        await Task.Delay(30, token);
        await PointerButton(button, 1, token);
        await Task.Delay(30, CancellationToken.None);
        await PointerButton(button, 0, CancellationToken.None);
        if (doubleClick)
        {
            await Task.Delay(60, token);
            await PointerButton(button, 1, token);
            await Task.Delay(30, CancellationToken.None);
            await PointerButton(button, 0, CancellationToken.None);
        }
    }

    private async Task PressKeyChordAsync(IReadOnlyList<int> symbols, CancellationToken token)
    {
        var pressed = new Stack<int>(symbols.Count);
        try
        {
            for (int i = 0; i < symbols.Count; i++)
            {
                await Key(symbols[i], 1, token);
                pressed.Push(symbols[i]);
                await Task.Delay(25, CancellationToken.None);
            }
        }
        finally
        {
            while (pressed.TryPop(out var symbol))
            {
                await Key(symbol, 0, CancellationToken.None);
                if (pressed.Count > 0) await Task.Delay(20, CancellationToken.None);
            }
        }
    }

    internal async Task MovePointerAsync(ComputerUseFrame frame, double x, double y, CancellationToken token)
    {
        var horizontal = MapPointerAxis(x, frame.Width, _width, _streamWidth);
        var vertical = MapPointerAxis(y, frame.Height, _height, _streamHeight);
        await Notify("NotifyPointerMotionAbsolute", "udd", (ref MessageWriter w) =>
        { w.WriteUInt32(_stream); w.WriteDouble(horizontal.Absolute); w.WriteDouble(vertical.Absolute); }, token);
        if (horizontal.Relative != 0 || vertical.Relative != 0)
            await Notify("NotifyPointerMotion", "dd", (ref MessageWriter w) =>
            { w.WriteDouble(horizontal.Relative); w.WriteDouble(vertical.Relative); }, token);
    }

    internal static (double Absolute, double Relative) MapPointerAxis(double pixel, int previewSize, int logicalSize, int streamSize)
    {
        // The portal validates absolute positions against the logical extent. Mutter's legacy
        // monitor stream interprets those positions in source pixels. Anchor inside both extents,
        // then finish the movement in compositor (logical) coordinates on the same monitor.
        var position = pixel * streamSize / previewSize;
        if (position < logicalSize) return (position, 0);
        var anchor = logicalSize - 1;
        return (anchor, (position - anchor) * logicalSize / streamSize);
    }

    private Task PointerButton(int button, uint state, CancellationToken token) =>
        Notify("NotifyPointerButton", "iu", (ref MessageWriter w) => { w.WriteInt32(button); w.WriteUInt32(state); }, token);

    private Task Key(int symbol, uint state, CancellationToken token) =>
        Notify("NotifyKeyboardKeysym", "iu", (ref MessageWriter w) => { w.WriteInt32(symbol); w.WriteUInt32(state); }, token);

    private static int KeySym(string key) => key switch
    {
        "ctrl" => 0xffe3, "shift" => 0xffe1, "alt" => 0xffe9, "cmd" or "super" => 0xffeb,
        "enter" => 0xff0d, "tab" => 0xff09, "escape" => 0xff1b, "space" => 32,
        "left" => 0xff51, "up" => 0xff52, "right" => 0xff53, "down" => 0xff54,
        "home" => 0xff50, "end" => 0xff57, "pageup" => 0xff55, "pagedown" => 0xff56,
        "backspace" => 0xff08, "delete" => 0xffff, _ when key.Length == 1 => key[0],
        _ when key[0] == 'f' && int.TryParse(key.AsSpan(1), out var n) && n is >= 1 and <= 24 => 0xffbe + n - 1,
        _ => throw new InvalidOperationException("Unsupported desktop key.")
    };

    private int _ended;
    public async Task EndAsync(CancellationToken token)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        try
        {
            if (!_closed && _session.Length != 0)
                await bus.CallMethodAsync(Message(Session, "Close", null, path: _session)).WaitAsync(TimeSpan.FromSeconds(2), token);
        }
        catch (Exception) when (_closed || !token.IsCancellationRequested) { /* Disconnect also closes portal sessions. */ }
        finally
        {
            _closed = true;
            _clipboard?.Dispose();
            _closedWatch?.Dispose();
            bus.Dispose();
            _video?.Dispose();
            _remote?.Dispose();
        }
    }
}
