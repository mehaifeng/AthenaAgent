using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Microsoft.Playwright;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Browser;

public class PlaywrightBrowserService : IHeadlessBrowserService, IAsyncDisposable
{
    private readonly IBrowserSessionManager _sessionManager;
    private readonly ISomAnnotator _somAnnotator;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly ConcurrentDictionary<string, RuntimeSession> _runtimeSessions = new();
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private bool _headless = true;

    public PlaywrightBrowserService(IBrowserSessionManager sessionManager, ISomAnnotator somAnnotator, ILogger logger)
    {
        _sessionManager = sessionManager;
        _somAnnotator = somAnnotator;
        _logger = logger.ForContext<PlaywrightBrowserService>();
    }

    public async Task<BrowserRuntimeStatus> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            await using var context = await browser.NewContextAsync();
            var page = await context.NewPageAsync();
            await page.SetContentAsync("<html><body>ok</body></html>");
            _ = await page.ScreenshotAsync(new PageScreenshotOptions { FullPage = false });

            return new BrowserRuntimeStatus
            {
                State = BrowserRuntimeState.Ready,
                Message = "Playwright Chromium runtime is ready."
            };
        }
        catch (PlaywrightException ex) when (LooksLikeMissingBrowser(ex))
        {
            return new BrowserRuntimeStatus
            {
                State = BrowserRuntimeState.BrowserNotInstalled,
                Message = "Playwright Chromium browser is not installed.",
                Details = ex.Message
            };
        }
        catch (Exception ex) when (ex is TypeLoadException or System.IO.FileNotFoundException)
        {
            return new BrowserRuntimeStatus
            {
                State = BrowserRuntimeState.PackageUnavailable,
                Message = "Microsoft.Playwright package is unavailable or incomplete.",
                Details = ex.Message
            };
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Browser runtime status check failed");
            return new BrowserRuntimeStatus
            {
                State = BrowserRuntimeState.Error,
                Message = "Browser runtime check failed.",
                Details = ex.Message
            };
        }
    }

    public Task<BrowserRuntimeInstallResult> InstallRuntimeAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var exitCode = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
                return new BrowserRuntimeInstallResult
                {
                    Success = exitCode == 0,
                    ExitCode = exitCode,
                    Message = exitCode == 0
                        ? "Playwright Chromium runtime installed."
                        : $"Playwright Chromium install failed with exit code {exitCode}."
                };
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Playwright Chromium install failed");
                return new BrowserRuntimeInstallResult
                {
                    Success = false,
                    ExitCode = -1,
                    Message = $"Playwright Chromium install failed: {ex.Message}"
                };
            }
        }, cancellationToken);
    }

    public async Task<BrowserSessionInfo> CreateSessionAsync(BrowserSessionOptions options, CancellationToken cancellationToken = default)
    {
        await CloseExpiredRuntimeSessionsAsync(cancellationToken);

        var browser = await EnsureBrowserAsync(options.Headless, cancellationToken);
        var session = await _sessionManager.CreateAsync(options, cancellationToken);
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            AcceptDownloads = options.DownloadEnabled,
            ViewportSize = new ViewportSize
            {
                Width = Math.Max(320, options.Viewport.Width),
                Height = Math.Max(240, options.Viewport.Height)
            },
            DeviceScaleFactor = (float)options.Viewport.DeviceScaleFactor
        });

        context.SetDefaultTimeout(options.OperationTimeoutSeconds * 1000);
        context.SetDefaultNavigationTimeout(options.OperationTimeoutSeconds * 1000);

        var page = await context.NewPageAsync();
        _runtimeSessions[session.SessionId] = new RuntimeSession(context, page, options);

        _logger.Information("Playwright runtime session initialized: {SessionId}", session.SessionId);
        return session;
    }

    public async Task<BrowserActionResult> NavigateAsync(string sessionId, string url, CancellationToken cancellationToken = default)
    {
        try
        {
            var runtimeSession = GetRuntimeSession(sessionId);
            await runtimeSession.Page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = runtimeSession.Options.OperationTimeoutSeconds * 1000
            });

            await _sessionManager.TouchAsync(sessionId, runtimeSession.Page.Url, cancellationToken);
            return BrowserActionSuccess(BrowserActionType.Navigate, sessionId, "Navigation completed.", runtimeSession.Page.Url);
        }
        catch (Exception ex)
        {
            return BrowserActionFailure(BrowserActionType.Navigate, sessionId, $"Navigation failed: {ex.Message}");
        }
    }

    public async Task<SomObservation> ObserveAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var runtimeSession = GetRuntimeSession(sessionId);

        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await WaitForPageStableAsync(runtimeSession);
                return await CaptureObservationAsync(sessionId, runtimeSession, cancellationToken);
            }
            catch (Exception ex) when (IsTransientNavigationException(ex) && attempt < maxAttempts)
            {
                _logger.Debug(ex, "Browser observation collided with navigation. Retrying. SessionId={SessionId}, Attempt={Attempt}", sessionId, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), cancellationToken);
            }
            catch (Exception ex) when (IsTransientNavigationException(ex))
            {
                throw new InvalidOperationException($"Observation failed because the page kept navigating: {ex.Message}", ex);
            }
        }

        throw new InvalidOperationException("Observation failed.");
    }

    public async Task<BrowserActionResult> ClickAsync(string sessionId, string elementId, CancellationToken cancellationToken = default)
    {
        try
        {
            var runtimeSession = GetRuntimeSession(sessionId);
            if (!runtimeSession.Elements.TryGetValue(elementId, out var element))
            {
                return BrowserActionFailure(BrowserActionType.Click, sessionId, $"Element not found in latest observation: {elementId}");
            }

            var box = element.BoundingBox;
            await runtimeSession.Page.Mouse.ClickAsync((float)(box.X + box.Width / 2), (float)(box.Y + box.Height / 2));
            await _sessionManager.TouchAsync(sessionId, runtimeSession.Page.Url, cancellationToken);
            return BrowserActionSuccess(BrowserActionType.Click, sessionId, "Click completed.", runtimeSession.Page.Url);
        }
        catch (Exception ex)
        {
            return BrowserActionFailure(BrowserActionType.Click, sessionId, $"Click failed: {ex.Message}");
        }
    }

    public async Task<BrowserActionResult> TypeAsync(string sessionId, string elementId, string text, CancellationToken cancellationToken = default)
    {
        try
        {
            var runtimeSession = GetRuntimeSession(sessionId);
            if (!runtimeSession.Elements.TryGetValue(elementId, out var element))
            {
                return BrowserActionFailure(BrowserActionType.Type, sessionId, $"Element not found in latest observation: {elementId}");
            }

            var box = element.BoundingBox;
            await runtimeSession.Page.Mouse.ClickAsync((float)(box.X + box.Width / 2), (float)(box.Y + box.Height / 2));
            await runtimeSession.Page.Keyboard.TypeAsync(text ?? string.Empty);
            await _sessionManager.TouchAsync(sessionId, runtimeSession.Page.Url, cancellationToken);
            return BrowserActionSuccess(BrowserActionType.Type, sessionId, "Type completed.", runtimeSession.Page.Url);
        }
        catch (Exception ex)
        {
            return BrowserActionFailure(BrowserActionType.Type, sessionId, $"Type failed: {ex.Message}");
        }
    }

    public async Task<BrowserActionResult> PressKeyAsync(string sessionId, string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var runtimeSession = GetRuntimeSession(sessionId);
            await runtimeSession.Page.Keyboard.PressAsync(key);
            await _sessionManager.TouchAsync(sessionId, runtimeSession.Page.Url, cancellationToken);
            return BrowserActionSuccess(BrowserActionType.PressKey, sessionId, "Key press completed.", runtimeSession.Page.Url);
        }
        catch (Exception ex)
        {
            return BrowserActionFailure(BrowserActionType.PressKey, sessionId, $"Key press failed: {ex.Message}");
        }
    }

    public async Task<BrowserActionResult> ScrollAsync(string sessionId, int deltaX, int deltaY, CancellationToken cancellationToken = default)
    {
        try
        {
            var runtimeSession = GetRuntimeSession(sessionId);
            await runtimeSession.Page.Mouse.WheelAsync(deltaX, deltaY);
            await _sessionManager.TouchAsync(sessionId, runtimeSession.Page.Url, cancellationToken);
            return BrowserActionSuccess(BrowserActionType.Scroll, sessionId, "Scroll completed.", runtimeSession.Page.Url);
        }
        catch (Exception ex)
        {
            return BrowserActionFailure(BrowserActionType.Scroll, sessionId, $"Scroll failed: {ex.Message}");
        }
    }

    public async Task<BrowserActionResult> WaitAsync(string sessionId, int milliseconds, CancellationToken cancellationToken = default)
    {
        try
        {
            var runtimeSession = GetRuntimeSession(sessionId);
            await runtimeSession.Page.WaitForTimeoutAsync(Math.Clamp(milliseconds, 0, 30000));
            await _sessionManager.TouchAsync(sessionId, runtimeSession.Page.Url, cancellationToken);
            return BrowserActionSuccess(BrowserActionType.Wait, sessionId, "Wait completed.", runtimeSession.Page.Url);
        }
        catch (Exception ex)
        {
            return BrowserActionFailure(BrowserActionType.Wait, sessionId, $"Wait failed: {ex.Message}");
        }
    }

    public async Task<BrowserActionResult> ExtractTextAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        try
        {
            var runtimeSession = GetRuntimeSession(sessionId);
            var text = await runtimeSession.Page.Locator("body").InnerTextAsync();
            await _sessionManager.TouchAsync(sessionId, runtimeSession.Page.Url, cancellationToken);
            return new BrowserActionResult
            {
                Success = true,
                Action = BrowserActionType.ExtractText,
                Message = "Text extracted.",
                SessionId = sessionId,
                Url = runtimeSession.Page.Url,
                ExtractedText = text
            };
        }
        catch (Exception ex)
        {
            return BrowserActionFailure(BrowserActionType.ExtractText, sessionId, $"Text extraction failed: {ex.Message}");
        }
    }

    public async Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (_runtimeSessions.TryRemove(sessionId, out var runtimeSession))
        {
            try
            {
                await runtimeSession.Context.CloseAsync();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to close browser context: {SessionId}", sessionId);
            }
        }

        await _sessionManager.CloseAsync(sessionId, cancellationToken);
    }

    public async Task<(bool Success, string Message)> TestRuntimeAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetRuntimeStatusAsync(cancellationToken);
        return (status.IsReady, status.Details == null ? status.Message : $"{status.Message} {status.Details}");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sessionId in _runtimeSessions.Keys.ToList())
        {
            await CloseSessionAsync(sessionId);
        }

        if (_browser != null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();
        _initializationLock.Dispose();
    }

    private async Task<IBrowser> EnsureBrowserAsync(bool headless, CancellationToken cancellationToken)
    {
        if (_browser?.IsConnected == true && _headless == headless)
        {
            return _browser;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_browser?.IsConnected == true && _headless == headless)
            {
                return _browser;
            }

            if (_browser != null)
            {
                await _browser.CloseAsync();
                _browser = null;
            }

            _playwright ??= await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = headless });
            _headless = headless;

            return _browser;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task CloseExpiredRuntimeSessionsAsync(CancellationToken cancellationToken)
    {
        var expiredIds = await _sessionManager.GetExpiredSessionIdsAsync(cancellationToken);
        foreach (var sessionId in expiredIds)
        {
            await CloseSessionAsync(sessionId, cancellationToken);
        }
    }

    private RuntimeSession GetRuntimeSession(string sessionId)
    {
        if (_runtimeSessions.TryGetValue(sessionId, out var runtimeSession))
        {
            return runtimeSession;
        }

        throw new InvalidOperationException($"Browser session not found: {sessionId}");
    }

    private static BrowserActionResult BrowserActionSuccess(BrowserActionType action, string sessionId, string message, string? url) =>
        new()
        {
            Success = true,
            Action = action,
            Message = message,
            SessionId = sessionId,
            Url = url
        };

    private static BrowserActionResult BrowserActionFailure(BrowserActionType action, string sessionId, string message) =>
        new()
        {
            Success = false,
            Action = action,
            Message = message,
            SessionId = sessionId
        };

    private static bool LooksLikeMissingBrowser(PlaywrightException ex) =>
        ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("playwright install", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Looks like Playwright was just installed or updated", StringComparison.OrdinalIgnoreCase);

    private async Task<SomObservation> CaptureObservationAsync(string sessionId, RuntimeSession runtimeSession, CancellationToken cancellationToken)
    {
        var screenshot = await runtimeSession.Page.ScreenshotAsync(new PageScreenshotOptions
        {
            FullPage = false,
            Type = ScreenshotType.Png
        });

        var title = await runtimeSession.Page.TitleAsync();
        var scroll = await runtimeSession.Page.EvaluateAsync<ScrollPosition>("() => ({ x: Math.round(window.scrollX), y: Math.round(window.scrollY) })");
        var viewport = runtimeSession.Page.ViewportSize;
        var viewportWidth = viewport?.Width ?? runtimeSession.Options.Viewport.Width;
        var viewportHeight = viewport?.Height ?? runtimeSession.Options.Viewport.Height;
        var elements = await ExtractInteractiveElementsAsync(runtimeSession, viewportWidth, viewportHeight);
        runtimeSession.Elements.Clear();
        foreach (var element in elements)
        {
            runtimeSession.Elements[element.ElementId] = element;
        }

        await _sessionManager.TouchAsync(sessionId, runtimeSession.Page.Url, cancellationToken);

        return await _somAnnotator.AnnotateAsync(new SomAnnotationRequest
        {
            SessionId = sessionId,
            Url = runtimeSession.Page.Url,
            Title = title,
            ScreenshotPng = screenshot,
            ViewportWidth = viewportWidth,
            ViewportHeight = viewportHeight,
            ScrollX = scroll.X,
            ScrollY = scroll.Y,
            MaxElements = runtimeSession.Options.SomMaxElements,
            IncludeElementText = runtimeSession.Options.SomIncludeText,
            Elements = elements
        }, cancellationToken);
    }

    private static async Task WaitForPageStableAsync(RuntimeSession runtimeSession)
    {
        try
        {
            await runtimeSession.Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
            {
                Timeout = 5000
            });
        }
        catch (TimeoutException)
        {
            // Dynamic pages can keep navigating; a later observe retry will catch real context loss.
        }

        try
        {
            await runtimeSession.Page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions
            {
                Timeout = 1000
            });
        }
        catch (TimeoutException)
        {
            // Network idle is a best-effort stabilizer only.
        }
    }

    private static bool IsTransientNavigationException(Exception ex)
    {
        if (ex is not PlaywrightException)
        {
            return false;
        }

        return ex.Message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("most likely because of a navigation", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("Cannot find context with specified id", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<List<SomElement>> ExtractInteractiveElementsAsync(RuntimeSession runtimeSession, int viewportWidth, int viewportHeight)
    {
        var candidatesJson = await runtimeSession.Page.EvaluateAsync<JsonElement>(InteractiveElementsScript);
        var maxElements = Math.Max(0, runtimeSession.Options.SomMaxElements);
        var includeText = runtimeSession.Options.SomIncludeText;
        var candidates = ParseInteractiveElements(candidatesJson);

        return candidates
            .Where(e => GetBool(e, "isVisible") && GetBool(e, "isEnabled") && GetDouble(e, "width") > 0 && GetDouble(e, "height") > 0)
            .Where(e => GetDouble(e, "x") < viewportWidth && GetDouble(e, "y") < viewportHeight && GetDouble(e, "x") + GetDouble(e, "width") > 0 && GetDouble(e, "y") + GetDouble(e, "height") > 0)
            .OrderBy(e => GetDouble(e, "y"))
            .ThenBy(e => GetDouble(e, "x"))
            .Take(maxElements)
            .Select((e, index) => new SomElement
            {
                Index = index + 1,
                ElementId = $"som-{index + 1}",
                TagName = GetString(e, "tagName") ?? string.Empty,
                Role = GetString(e, "role"),
                Text = includeText ? Truncate(GetString(e, "text"), 120) : null,
                AriaLabel = includeText ? Truncate(GetString(e, "ariaLabel"), 120) : null,
                Placeholder = includeText ? Truncate(GetString(e, "placeholder"), 120) : null,
                Href = Truncate(GetString(e, "href"), 240),
                Selector = GetString(e, "selector"),
                IsVisible = GetBool(e, "isVisible"),
                IsEnabled = GetBool(e, "isEnabled"),
                BoundingBox = new BrowserBoundingBox
                {
                    X = Math.Max(0, GetDouble(e, "x")),
                    Y = Math.Max(0, GetDouble(e, "y")),
                    Width = Math.Min(GetDouble(e, "width"), viewportWidth),
                    Height = Math.Min(GetDouble(e, "height"), viewportHeight)
                }
            })
            .ToList();
    }

    private static List<JsonElement> ParseInteractiveElements(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Array)
        {
            return new List<JsonElement>();
        }

        var elements = new List<JsonElement>();
        foreach (var item in json.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                elements.Add(item.Clone());
            }
        }

        return elements;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static double GetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => double.IsFinite(number) ? number : 0,
            JsonValueKind.String when double.TryParse(value.GetString(), out var number) => double.IsFinite(number) ? number : 0,
            _ => 0
        };
    }

    private static bool GetBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var result) && result,
            _ => false
        };
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private const string InteractiveElementsScript = """
        () => {
          const selector = [
            'a[href]',
            'button',
            'input',
            'textarea',
            'select',
            '[role="button"]',
            '[role="link"]',
            '[role="menuitem"]',
            '[role="tab"]',
            '[tabindex]',
            '[contenteditable="true"]',
            '[onclick]'
          ].join(',');

          const cssPath = (el) => {
            if (!el || !el.tagName) return '';
            if (el.id) return `#${CSS.escape(el.id)}`;
            const parts = [];
            let node = el;
            while (node && node.nodeType === Node.ELEMENT_NODE && parts.length < 5) {
              let part = node.tagName.toLowerCase();
              const parent = node.parentElement;
              if (parent) {
                const siblings = Array.from(parent.children).filter(child => child.tagName === node.tagName);
                if (siblings.length > 1) part += `:nth-of-type(${siblings.indexOf(node) + 1})`;
              }
              parts.unshift(part);
              node = parent;
            }
            return parts.join(' > ');
          };

          const isVisible = (el, rect) => {
            const style = window.getComputedStyle(el);
            return style &&
              style.visibility !== 'hidden' &&
              style.display !== 'none' &&
              Number(style.opacity || '1') > 0 &&
              rect.width > 0 &&
              rect.height > 0;
          };

          const isEnabled = (el) => {
            return !el.disabled && el.getAttribute('aria-disabled') !== 'true';
          };

          const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();
          const elements = [];
          const seen = new Set();

          for (const el of Array.from(document.querySelectorAll(selector))) {
            if (seen.has(el)) continue;
            seen.add(el);

            const rect = el.getBoundingClientRect();
            if (!isVisible(el, rect)) continue;

            elements.push({
              tagName: el.tagName.toLowerCase(),
              role: el.getAttribute('role') || '',
              text: normalize(el.innerText || el.textContent || el.value || ''),
              ariaLabel: normalize(el.getAttribute('aria-label') || ''),
              placeholder: normalize(el.getAttribute('placeholder') || ''),
              href: el.href || el.getAttribute('href') || '',
              selector: cssPath(el),
              x: rect.x,
              y: rect.y,
              width: rect.width,
              height: rect.height,
              isVisible: true,
              isEnabled: isEnabled(el)
            });
          }

          return elements;
        }
        """;

    private sealed record RuntimeSession(IBrowserContext Context, IPage Page, BrowserSessionOptions Options)
    {
        public ConcurrentDictionary<string, SomElement> Elements { get; } = new();
    }

    private sealed class ScrollPosition
    {
        public int X { get; set; }
        public int Y { get; set; }
    }
}
