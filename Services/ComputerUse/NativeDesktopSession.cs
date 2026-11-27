using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.ComputerUse;

// The C ABI uses the same desktop action contract on Windows, macOS and X11.
internal sealed class NativeDesktopSession(string id,
    Func<string, object, CancellationToken, Task<JsonElement>> call) : IComputerUseSession
{
    private ComputerUseFrame? _latest;
    private int _screenWidth, _screenHeight;

    public async Task<ComputerUseFrame> ObserveAsync(CancellationToken token)
    {
        var result = await call("get_desktop_state", new { session = id, max_image_dimension = 1440 }, token);
        var state = result.GetProperty("structuredContent");
        var image = result.GetProperty("content").EnumerateArray()
            .FirstOrDefault(item => item.GetProperty("type").GetString() == "image");
        if (image.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Desktop capture failed. On macOS grant Athena Screen Recording and Accessibility access.");
        _screenWidth = state.GetProperty("screen_width").GetInt32();
        _screenHeight = state.GetProperty("screen_height").GetInt32();
        _latest = new(image.GetProperty("data").GetBytesFromBase64(), image.GetProperty("mimeType").GetString()!,
            state.GetProperty("screenshot_width").GetInt32(), state.GetProperty("screenshot_height").GetInt32(),
            $"Current desktop ({Environment.OSVersion.Platform}). Keyboard input goes to the foreground app.",
            state.GetProperty("capture_id").GetString(), [], IsDesktop: true);
        return _latest;
    }

    public async Task<string> ActAsync(ComputerUseFrame frame, ComputerUseAction action, double x, double y, CancellationToken token)
    {
        if (!ReferenceEquals(frame, _latest)) throw new InvalidOperationException("Observe the desktop again before sending input.");
        _latest = null;
        var arguments = new Dictionary<string, object> { ["session"] = id, ["scope"] = "desktop" };
        var tool = action.Action;
        switch (tool)
        {
            case "click" or "double_click" or "right_click":
                tool = "click";
                if (action.Action == "double_click") arguments["count"] = 2;
                if (action.Action == "right_click") arguments["button"] = "right";
                arguments["delivery_mode"] = "foreground";
                arguments["x"] = x;
                arguments["y"] = y;
                arguments["capture_id"] = frame.CaptureId!;
                break;
            case "scroll":
                arguments["x"] = OperatingSystem.IsMacOS() ? x : Math.Floor(x * _screenWidth / frame.Width);
                arguments["y"] = OperatingSystem.IsMacOS() ? y : Math.Floor(y * _screenHeight / frame.Height);
                arguments["direction"] = action.Direction!;
                break;
            case "type_text":
                if (action.X is not null && action.Y is not null)
                {
                    await call("click", new Dictionary<string, object>
                    {
                        ["session"] = id,
                        ["scope"] = "desktop",
                        ["delivery_mode"] = "foreground",
                        ["x"] = x,
                        ["y"] = y,
                        ["capture_id"] = frame.CaptureId!
                    }, token);
                    await Task.Delay(140, token);
                }
                arguments["text"] = action.Text!;
                break;
            case "press_key":
                if (!DesktopKeys.TryParse(action.Key, out var keys)) throw new InvalidOperationException("Unsupported desktop key.");
                if (keys.Length == 1) arguments["key"] = keys[0];
                else { tool = "hotkey"; arguments["keys"] = keys; }
                break;
            default: throw new InvalidOperationException("Unsupported desktop action.");
        }
        var result = await call(tool, arguments, token);
        return string.Join("\n", result.GetProperty("content").EnumerateArray().Where(item => item.GetProperty("type").GetString() == "text")
            .Select(item => item.GetProperty("text").GetString()));
    }

    public Task EndAsync(CancellationToken token) => call("end_session", new { session = id }, token);
}

internal static class DesktopKeys
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = "ctrl", ["Control"] = "ctrl", ["Shift"] = "shift", ["Alt"] = "alt", ["Option"] = "alt",
        ["Super_L"] = "super", ["Meta"] = "super", ["Control_L"] = "ctrl", ["Alt_L"] = "alt",
        ["Cmd"] = "cmd", ["Command"] = "cmd", ["Super"] = "super", ["Win"] = "super",
        ["Esc"] = "escape", ["Return"] = "enter", ["Enter"] = "enter", ["Escape"] = "escape", ["Tab"] = "tab", ["space"] = "space",
        ["Left"] = "left", ["Right"] = "right", ["Up"] = "up", ["Down"] = "down", ["Home"] = "home", ["End"] = "end",
        ["Page_Up"] = "pageup", ["PageUp"] = "pageup", ["Page_Down"] = "pagedown", ["PageDown"] = "pagedown", ["BackSpace"] = "backspace", ["Delete"] = "delete"
    };

    internal static bool TryParse(string? key, out string[] keys)
    {
        keys = [];
        if (string.IsNullOrWhiteSpace(key)) return false;
        var parts = key.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length > 4) return false;
        var normalized = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (!Names.TryGetValue(part, out var value))
            {
                bool functionKey = part.Length is 2 or 3 && (part[0] == 'F' || part[0] == 'f')
                    && int.TryParse(part.AsSpan(1), out var number) && number is >= 1 and <= 24;
                if (!functionKey && (part.Length != 1 || !char.IsAsciiLetterOrDigit(part[0]))) return false;
                value = part.ToLowerInvariant();
            }
            if (normalized.Contains(value)) return false;
            normalized.Add(value);
        }
        if (normalized.SkipLast(1).Any(k => k is not ("ctrl" or "alt" or "shift" or "cmd" or "super"))) return false;
        keys = normalized.ToArray();
        return true;
    }
}
