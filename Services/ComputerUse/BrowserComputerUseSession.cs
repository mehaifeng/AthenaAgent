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

internal sealed class BrowserComputerUseSession(EmbeddedBrowser browser, ChromeBrowserPage initialPage) : IComputerUseSession
{
    private const int TypingIntervalMilliseconds = 120;
    private const int TypingPauseBudgetMilliseconds = 2000;
    private const double HorizontalScrollFraction = 0.8;

    private static readonly Lazy<string> PageScript = new(() =>
    {
        using var resource = typeof(BrowserComputerUseSession).Assembly
            .GetManifestResourceStream("Athena.UI.ComputerUse.BrowserPage.js")
            ?? throw new InvalidOperationException("The browser Computer Use page script is missing.");
        using var reader = new StreamReader(resource);
        return reader.ReadToEnd();
    });
    private int _context;
    private ChromeBrowserPage _page = initialPage;
    private readonly BrowserViewportSize _initialViewport = initialPage.ViewportSize ?? new(800, 720);
    private Viewport? _viewport;
    private int _viewportRevision;

    private ChromeDevTools DevTools => _page.Driver;

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
        var selected = browser.TaskPage ?? throw new InvalidOperationException("The browser task tab was closed.");
        if (!ReferenceEquals(selected, _page))
        {
            _page.Cursor = null;
            _page = selected;
            _context = 0;
            _viewport = null;
        }
        if (_page.ViewportSize == null)
            await _page.InitializeViewportAsync(_initialViewport, token).ConfigureAwait(false);
        using var viewportLease = await _page.LockViewportAsync(token).ConfigureAwait(false);
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
        if (CefNativeBridge.IsHeadlessRuntime)
        {
            if (_page.ViewportSize is not { } expected
                || viewport.Width != expected.Width || viewport.Height != expected.Height
                || Math.Abs(viewport.Scale - expected.Scale) > 0.0001)
                return null;
        }
        if (!Uri.TryCreate(viewport.Url, UriKind.Absolute, out var document) || !EmbeddedBrowser.IsPageUri(document))
            throw new InvalidOperationException("Computer Use reached a tab outside HTTP and HTTPS webpages.");
        var capture = await DevTools.CallAsync("Page.captureScreenshot",
            new { format = "png", captureBeyondViewport = false }, token).ConfigureAwait(false);
        var image = capture.GetProperty("data").GetBytesFromBase64();
        var width = BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(20, 4));
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("Chrome returned an empty viewport image.");

        var expectedPixels = new BrowserViewportSize(viewport.Width, viewport.Height, viewport.Scale);
        // Recheck after capture so a scroll or resize cannot pair the screenshot with stale controls.
        // Chromium may round a fractional device scale down by one pixel for a capture.
        if (!ReferenceEquals(_page, browser.TaskPage)
            || Math.Abs(width - expectedPixels.PixelWidth) > 2 || Math.Abs(height - expectedPixels.PixelHeight) > 2
            || viewport != ReadViewport(await EvaluateAsync("athenaPage.viewport()", token).ConfigureAwait(false)))
            return null;
        _viewport = viewport;
        _viewportRevision = _page.ViewportRevision;
        var text = new StringBuilder()
            .AppendLine(state.GetProperty("title").GetString())
            .AppendLine(document.AbsoluteUri);
        var horizontal = state.GetProperty("horizontalPageDirections");
        if (horizontal.GetArrayLength() > 0)
            text.AppendLine($"Horizontal page scroll available: {string.Join(", ", horizontal.EnumerateArray().Select(direction => direction.GetString()))}.");
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
        return new(image, "image/png", width, height, text.ToString(), null, elements, BrowserUri: document);
    }

    public async Task<string> NavigateAsync(Uri address, CancellationToken token)
    {
        if (!ReferenceEquals(_page, browser.TaskPage))
            return "The active browser tab changed. Observe the current tab first.";
        await _page.NavigateAsync(address, token).ConfigureAwait(false);
        return $"Chrome started navigation to {address.AbsoluteUri}. Check the next observation for the loaded page.";
    }

    public async Task<string> ActAsync(ComputerUseFrame frame, ComputerUseAction action, double x, double y, CancellationToken token)
    {
        // Complete the input against the observed layout before applying another panel size.
        using var viewportLease = await _page.LockViewportAsync(token).ConfigureAwait(false);
        if (!ReferenceEquals(_page, browser.TaskPage) || _viewportRevision != _page.ViewportRevision)
            return "The browser viewport changed. Observe it again and choose the target from the new screenshot.";
        var viewport = _viewport ?? throw new InvalidOperationException("Observe the page before sending input.");
        x *= viewport.Width / (double)frame.Width;
        y *= viewport.Height / (double)frame.Height;
        var coordinates = FormattableString.Invariant($"{x},{y}");
        var index = action.Element?.ToString(CultureInfo.InvariantCulture) ?? "null";
        var check = $"athenaPage.hits({index},{coordinates})";
        if (!ReferenceEquals(_page, browser.TaskPage) || viewport != ReadViewport(await EvaluateAsync("athenaPage.viewport()", token).ConfigureAwait(false))
            || !(await EvaluateAsync(check, token).ConfigureAwait(false)).GetBoolean())
            return "The page moved before input. Observe it again and choose the target from the new screenshot.";

        _page.SetAgentInputActive(true);
        try
        {
            if (action.Action == "press_key")
            {
                await PressKeyAsync(DevTools, action.Key!, token).ConfigureAwait(false);
                return $"Chrome received press_key ({action.Key}). Check the updated page for its effect.";
            }

            _page.Cursor = ComputerUseCursor.Move(_page.Cursor, x / viewport.Width, y / viewport.Height);
            await Task.Delay(ComputerUseCursor.MoveMilliseconds, token).ConfigureAwait(false);
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
                    var deltaX = 0.0;
                    if (action.Direction is "left" or "right")
                    {
                        var width = await EvaluateAsync($"athenaPage.horizontalScrollWidth({coordinates})", token).ConfigureAwait(false);
                        // CEF uses native deviceScaleFactor rather than page zoom, so CDP mouseWheel takes CSS pixels directly.
                        deltaX = Math.Max(1, width.GetDouble() * HorizontalScrollFraction)
                            * (action.Direction == "left" ? -1 : 1);
                    }
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
        finally
        {
            _page.SetAgentInputActive(false);
        }
    }

    private async Task ClickAsync(double x, double y, CancellationToken token)
    {
        if (_page.Cursor is { } cursor)
            _page.Cursor = cursor.Click();
        await DevTools.CallAsync("Input.dispatchMouseEvent",
            new { type = "mousePressed", x, y, button = "left", buttons = 1, clickCount = 1 }, token).ConfigureAwait(false);
        await DevTools.CallAsync("Input.dispatchMouseEvent",
            new { type = "mouseReleased", x, y, button = "left", buttons = 0, clickCount = 1 }, token).ConfigureAwait(false);
    }

    private async Task TypeAsync(string text, CancellationToken token)
    {
        if (_page.Cursor is { } cursor)
            _page.Cursor = cursor with { IsTyping = true };
        var glyphs = StringInfo.ParseCombiningCharacters(text);
        var pause = Math.Min(TypingIntervalMilliseconds, TypingPauseBudgetMilliseconds / Math.Max(1, glyphs.Length));
        for (var index = 0; index < glyphs.Length; index++)
        {
            var end = index + 1 < glyphs.Length ? glyphs[index + 1] : text.Length;
            await TypeCharacterAsync(DevTools, text[glyphs[index]..end], token).ConfigureAwait(false);
            if (pause > 0)
                await Task.Delay(pause, token).ConfigureAwait(false);
        }
        if (_page.Cursor is { } typing)
            _page.Cursor = typing with { IsTyping = false };
    }

    internal static async Task TypeCommittedTextAsync(ChromeDevTools driver, string text, CancellationToken token)
    {
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
            await TypeCharacterAsync(driver, elements.GetTextElement(), token).ConfigureAwait(false);
    }

    private static Task TypeCharacterAsync(ChromeDevTools driver, string text, CancellationToken token)
    {
        // Keep composed glyphs intact and insert control characters as text, without running key commands.
        if (!Rune.TryGetRuneAt(text, 0, out var character) || character.Utf16SequenceLength != text.Length || Rune.IsControl(character))
            return driver.CallAsync("Input.insertText", new { text }, token);

        var (code, virtualKey, modifiers) = text[0] switch
        {
            >= 'a' and <= 'z' => ($"Key{char.ToUpperInvariant(text[0])}", (int)char.ToUpperInvariant(text[0]), 0),
            >= 'A' and <= 'Z' => ($"Key{text}", (int)text[0], 8),
            >= '0' and <= '9' => ($"Digit{text}", (int)text[0], 0),
            ' ' => ("Space", 32, 0),
            _ => ("", 0, 0)
        };
        return DispatchKeyAsync(driver, text, code, virtualKey, text, token, modifiers);
    }

    internal static Task PressKeyAsync(ChromeDevTools driver, string key, CancellationToken token)
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
        return DispatchKeyAsync(driver, name, code, virtualKey, text, token);
    }

    private static async Task DispatchKeyAsync(ChromeDevTools driver, string key, string code, int virtualKey, string text,
        CancellationToken token, int modifiers = 0)
    {
        await driver.CallAsync("Input.dispatchKeyEvent",
            new { type = text.Length == 0 ? "rawKeyDown" : "keyDown", key, code, windowsVirtualKeyCode = virtualKey, text, modifiers }, token)
            .ConfigureAwait(false);
        await driver.CallAsync("Input.dispatchKeyEvent",
            new { type = "keyUp", key, code, windowsVirtualKeyCode = virtualKey, modifiers }, token).ConfigureAwait(false);
    }

    private async Task<JsonElement> EvaluateAsync(string expression, CancellationToken token)
    {
        var response = await DevTools.CallAsync("Runtime.evaluate",
            new { expression, contextId = _context, returnByValue = true, awaitPromise = true }, token).ConfigureAwait(false);
        if (response.TryGetProperty("exceptionDetails", out _))
            throw new InvalidOperationException("Chrome could not inspect the current webpage.");
        var result = response.GetProperty("result");
        return result.TryGetProperty("value", out var value) ? value : default;
    }

    public Task EndAsync(CancellationToken token)
    {
        _page.Cursor = null;
        return Task.CompletedTask;
    }

    private static Viewport ReadViewport(JsonElement state) => new(
        state.GetProperty("width").GetInt32(), state.GetProperty("height").GetInt32(),
        state.GetProperty("scale").GetDouble(),
        state.GetProperty("scrollX").GetDouble(), state.GetProperty("scrollY").GetDouble(),
        state.GetProperty("url").GetString() ?? "");

    private sealed record Viewport(int Width, int Height, double Scale, double ScrollX, double ScrollY, string Url);
}
