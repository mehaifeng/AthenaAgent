using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.ComputerUse;

internal sealed class BrowserComputerUseSession(ChromeBrowserPage page) : IComputerUseSession
{
    private static readonly Lazy<string> PageScript = new(() =>
    {
        using var resource = typeof(BrowserComputerUseSession).Assembly
            .GetManifestResourceStream("Athena.UI.ComputerUse.BrowserPage.js")
            ?? throw new InvalidOperationException("The browser Computer Use page script is missing.");
        using var reader = new StreamReader(resource);
        return reader.ReadToEnd();
    });
    private int _context;
    private Viewport? _viewport;

    private ChromeDevTools DevTools => page.Driver;

    public async Task<ComputerUseFrame> ObserveAsync(CancellationToken token)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var frame = await TryObserveAsync(token).ConfigureAwait(false);
                if (frame is not null)
                    return frame;
            }
            catch (ChromeDevToolsException exception) when (exception.IsNavigation)
            {
                // A click can replace the main frame while Chrome is being read.
            }
            await Task.Delay(200, token).ConfigureAwait(false);
        }
        throw new InvalidOperationException("Chrome did not produce a usable page observation.");
    }

    private async Task<ComputerUseFrame?> TryObserveAsync(CancellationToken token)
    {
        var frames = await DevTools.CallAsync("Page.getFrameTree", new { }, token).ConfigureAwait(false);
        var main = frames.GetProperty("frameTree").GetProperty("frame");
        var url = main.GetProperty("url").GetString();
        if (url == "about:blank")
            return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var initialAddress) || !EmbeddedBrowser.IsPageUri(initialAddress))
            throw new InvalidOperationException("Computer Use reached a tab outside HTTP and HTTPS webpages.");

        var world = await DevTools.CallAsync("Page.createIsolatedWorld",
            new { frameId = main.GetProperty("id").GetString(), worldName = "Athena Computer Use" }, token).ConfigureAwait(false);
        _context = world.GetProperty("executionContextId").GetInt32();
        var state = await EvaluateAsync(PageScript.Value + "\nathenaPage.observe()", token).ConfigureAwait(false);
        if (!state.GetProperty("ready").GetBoolean())
            return null;

        var viewport = ReadViewport(state);
        if (viewport.Width <= 0 || viewport.Height <= 0)
            return null;
        if (!Uri.TryCreate(viewport.Url, UriKind.Absolute, out var document) || !EmbeddedBrowser.IsPageUri(document))
            throw new InvalidOperationException("Computer Use reached a tab outside HTTP and HTTPS webpages.");
        var capture = await DevTools.CallAsync("Page.captureScreenshot",
            new { format = "png", captureBeyondViewport = false }, token).ConfigureAwait(false);
        var image = capture.GetProperty("data").GetBytesFromBase64();
        var width = BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(20, 4));
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("Chrome returned an empty viewport image.");

        // Recheck after capture so a scroll or resize cannot pair the screenshot with stale controls.
        if (viewport != ReadViewport(await EvaluateAsync("athenaPage.viewport()", token).ConfigureAwait(false)))
            return null;
        page.UpdateAddress(document);
        _viewport = viewport;
        var text = new StringBuilder()
            .AppendLine(state.GetProperty("title").GetString())
            .AppendLine(document.AbsoluteUri);
        var elements = new List<ComputerUseElement>();
        foreach (var control in state.GetProperty("controls").EnumerateArray())
        {
            var index = elements.Count + 1;
            var label = control.GetProperty("label").GetString() ?? "";
            var role = control.GetProperty("role").GetString() ?? "";
            var x = control.GetProperty("x").GetDouble() * width / viewport.Width;
            var y = control.GetProperty("y").GetDouble() * height / viewport.Height;
            elements.Add(new(index, "", role, label, x, y));
            text.AppendLine(CultureInfo.InvariantCulture, $"[{index}] {role} {label} at ({x:F0}, {y:F0})");
        }
        text.AppendLine(state.GetProperty("visibleText").GetString());
        return new(image, "image/png", width, height, text.ToString(), null, elements, CursorInPage: true, BrowserUri: document);
    }

    public async Task<string> NavigateAsync(Uri address, CancellationToken token)
    {
        await page.NavigateAsync(address, token).ConfigureAwait(false);
        return $"Chrome started navigation to {address.AbsoluteUri}. Check the next observation for the loaded page.";
    }

    public async Task<string> ActAsync(ComputerUseFrame frame, ComputerUseAction action, double x, double y, CancellationToken token)
    {
        var viewport = _viewport ?? throw new InvalidOperationException("Observe the page before sending input.");
        x *= viewport.Width / (double)frame.Width;
        y *= viewport.Height / (double)frame.Height;
        var coordinates = FormattableString.Invariant($"{x},{y}");
        var index = action.Element?.ToString(CultureInfo.InvariantCulture) ?? "null";
        var check = $"athenaPage.hits({index},{coordinates})";
        if (viewport != ReadViewport(await EvaluateAsync("athenaPage.viewport()", token).ConfigureAwait(false))
            || !(await EvaluateAsync(check, token).ConfigureAwait(false)).GetBoolean())
            return "The page moved before input. Observe it again and choose the target from the new screenshot.";

        if (action.Action == "press_key")
        {
            await PressKeyAsync(DevTools, action.Key!, token).ConfigureAwait(false);
            return $"Chrome received press_key ({action.Key}). Check the updated page for its effect.";
        }

        await EvaluateAsync($"athenaPage.move({coordinates})", token).ConfigureAwait(false);
        await DevTools.CallAsync("Input.dispatchMouseEvent", new { type = "mouseMoved", x, y }, token).ConfigureAwait(false);
        if (viewport != ReadViewport(await EvaluateAsync("athenaPage.viewport()", token).ConfigureAwait(false))
            || !(await EvaluateAsync(check, token).ConfigureAwait(false)).GetBoolean())
            return "The target moved during the cursor animation. Observe again before sending input.";

        switch (action.Action)
        {
            case "click":
                await ClickAsync(x, y, token).ConfigureAwait(false);
                break;
            case "type_text":
                await ClickAsync(x, y, token).ConfigureAwait(false);
                if (!(await EvaluateAsync("athenaPage.hasEditableFocus()", token).ConfigureAwait(false)).GetBoolean())
                    return "The click did not focus an editable field. No text was entered.";
                await TypeAsync(action.Text!, token).ConfigureAwait(false);
                break;
            case "scroll":
                var deltaX = action.Direction switch { "left" => -540, "right" => 540, _ => 0 };
                var deltaY = action.Direction switch { "up" => -540, "down" => 540, _ => 0 };
                await DevTools.CallAsync("Input.dispatchMouseEvent",
                    new { type = "mouseWheel", x, y, deltaX, deltaY }, token).ConfigureAwait(false);
                break;
        }
        var target = action.Element is { } selected
            ? frame.Elements.First(e => e.Index == selected).Label
            : "the selected point";
        return $"Chrome received {action.Action} at {target}. Check the updated page for its effect.";
    }

    private async Task ClickAsync(double x, double y, CancellationToken token)
    {
        await EvaluateAsync("athenaPage.clickPulse()", token).ConfigureAwait(false);
        await DevTools.CallAsync("Input.dispatchMouseEvent",
            new { type = "mousePressed", x, y, button = "left", buttons = 1, clickCount = 1 }, token).ConfigureAwait(false);
        await DevTools.CallAsync("Input.dispatchMouseEvent",
            new { type = "mouseReleased", x, y, button = "left", buttons = 0, clickCount = 1 }, token).ConfigureAwait(false);
    }

    private async Task TypeAsync(string text, CancellationToken token)
    {
        await EvaluateAsync("athenaPage.typing(true)", token).ConfigureAwait(false);
        var glyphs = StringInfo.ParseCombiningCharacters(text);
        var pace = Math.Max(1, (glyphs.Length + 31) / 32);
        for (var index = 0; index < glyphs.Length; index += pace)
        {
            var end = index + pace < glyphs.Length ? glyphs[index + pace] : text.Length;
            await DevTools.CallAsync("Input.insertText", new { text = text[glyphs[index]..end] }, token).ConfigureAwait(false);
            await Task.Delay(34, token).ConfigureAwait(false);
        }
        await EvaluateAsync("athenaPage.typing(false)", token).ConfigureAwait(false);
    }

    internal static async Task PressKeyAsync(ChromeDevTools driver, string key, CancellationToken token)
    {
        var (name, code, virtualKey, text) = key switch
        {
            "Return" => ("Enter", "Enter", 13, "\r"),
            "Tab" => ("Tab", "Tab", 9, ""),
            "Escape" => ("Escape", "Escape", 27, ""),
            "Left" => ("ArrowLeft", "ArrowLeft", 37, ""),
            "Up" => ("ArrowUp", "ArrowUp", 38, ""),
            "Right" => ("ArrowRight", "ArrowRight", 39, ""),
            "Down" => ("ArrowDown", "ArrowDown", 40, ""),
            "Home" => ("Home", "Home", 36, ""),
            "End" => ("End", "End", 35, ""),
            "Page_Up" => ("PageUp", "PageUp", 33, ""),
            "Page_Down" => ("PageDown", "PageDown", 34, ""),
            "BackSpace" => ("Backspace", "Backspace", 8, ""),
            "Delete" => ("Delete", "Delete", 46, ""),
            "space" => (" ", "Space", 32, " "),
            _ => throw new InvalidOperationException("Unsupported Chrome key.")
        };
        await driver.CallAsync("Input.dispatchKeyEvent",
            new { type = text.Length == 0 ? "rawKeyDown" : "keyDown", key = name, code, windowsVirtualKeyCode = virtualKey, text }, token)
            .ConfigureAwait(false);
        await driver.CallAsync("Input.dispatchKeyEvent",
            new { type = "keyUp", key = name, code, windowsVirtualKeyCode = virtualKey }, token).ConfigureAwait(false);
    }

    private async Task<JsonElement> EvaluateAsync(string expression, CancellationToken token)
    {
        var response = await DevTools.CallAsync("Runtime.evaluate",
            new { expression, contextId = _context, returnByValue = true, awaitPromise = true }, token).ConfigureAwait(false);
        if (response.TryGetProperty("exceptionDetails", out _))
            throw new InvalidOperationException("Chrome could not observe or display the Computer Use cursor.");
        var result = response.GetProperty("result");
        return result.TryGetProperty("value", out var value) ? value : default;
    }

    public async Task EndAsync(CancellationToken token)
    {
        if (_context != 0)
            await EvaluateAsync("globalThis.athenaPage?.remove()", token).ConfigureAwait(false);
    }

    private static Viewport ReadViewport(JsonElement state) => new(
        state.GetProperty("width").GetInt32(), state.GetProperty("height").GetInt32(),
        state.GetProperty("scrollX").GetDouble(), state.GetProperty("scrollY").GetDouble(),
        state.GetProperty("url").GetString() ?? "");

    private sealed record Viewport(int Width, int Height, double ScrollX, double ScrollY, string Url);
}
