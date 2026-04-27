using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Microsoft.Playwright;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
            return new BrowserActionResult
            {
                Success = true,
                Action = BrowserActionType.Click,
                Message = "Click completed.",
                SessionId = sessionId,
                Url = runtimeSession.Page.Url,
                Effect = new BrowserActionEffect
                {
                    ElementId = element.ElementId,
                    TargetStableKey = element.StableKey,
                    TargetSelector = element.Selector,
                    RequestedText = DescribeElement(element),
                    ValueBefore = element.IsSensitive ? null : element.Value,
                    ValueAfter = element.IsSensitive ? null : element.Value,
                    Changed = false,
                    Skipped = false,
                    MatchesRequestedValue = false,
                    SkipReason = null
                }
            };
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
            var requestedText = text ?? string.Empty;
            var centerX = box.X + box.Width / 2;
            var centerY = box.Y + box.Height / 2;

            await runtimeSession.Page.Mouse.ClickAsync((float)centerX, (float)centerY);
            var fillResult = await runtimeSession.Page.EvaluateAsync<TypeFillResult>(FillEditableElementScript, new
            {
                x = centerX,
                y = centerY,
                text = requestedText
            });

            if (fillResult == null)
            {
                return BrowserActionFailure(BrowserActionType.Type, sessionId, "Type failed: no fill result was returned.");
            }

            var effect = new BrowserActionEffect
            {
                ElementId = element.ElementId,
                TargetStableKey = element.StableKey,
                TargetSelector = element.Selector,
                RequestedText = fillResult.IsSensitive ? null : requestedText,
                ValueBefore = fillResult.ValueBefore,
                ValueAfter = fillResult.ValueAfter,
                Changed = fillResult.Changed,
                Skipped = fillResult.Skipped,
                MatchesRequestedValue = fillResult.MatchesRequestedValue,
                SkipReason = fillResult.SkipReason
            };

            await _sessionManager.TouchAsync(sessionId, runtimeSession.Page.Url, cancellationToken);
            if (!fillResult.Success)
            {
                return new BrowserActionResult
                {
                    Success = false,
                    Action = BrowserActionType.Type,
                    Message = $"Type failed: {fillResult.Error ?? "target value did not change as expected."}",
                    SessionId = sessionId,
                    Url = runtimeSession.Page.Url,
                    Effect = effect
                };
            }

            var message = fillResult.Skipped
                ? "Type skipped: target already contains the requested value."
                : "Type completed: target value verified.";

            return new BrowserActionResult
            {
                Success = true,
                Action = BrowserActionType.Type,
                Message = message,
                SessionId = sessionId,
                Url = runtimeSession.Page.Url,
                Effect = effect
            };
        }
        catch (Exception ex)
        {
            return BrowserActionFailure(BrowserActionType.Type, sessionId, $"Type failed: {ex.Message}");
        }
    }

    public async Task<BrowserActionResult> SelectAsync(string sessionId, string elementId, string optionText, CancellationToken cancellationToken = default)
    {
        try
        {
            var runtimeSession = GetRuntimeSession(sessionId);
            if (!runtimeSession.Elements.TryGetValue(elementId, out var element))
            {
                return BrowserActionFailure(BrowserActionType.Type, sessionId, $"Element not found in latest observation: {elementId}");
            }

            var box = element.BoundingBox;
            var requestedText = optionText ?? string.Empty;
            var result = await runtimeSession.Page.EvaluateAsync<SelectControlResult>(SelectElementOptionScript, new
            {
                x = box.X + box.Width / 2,
                y = box.Y + box.Height / 2,
                text = requestedText
            });

            if (result == null)
            {
                return BrowserActionFailure(BrowserActionType.Type, sessionId, "Select failed: no select result was returned.");
            }

            var effect = new BrowserActionEffect
            {
                ElementId = element.ElementId,
                TargetStableKey = element.StableKey,
                TargetSelector = element.Selector,
                RequestedText = result.SelectedText ?? requestedText,
                ValueBefore = result.ValueBefore,
                ValueAfter = result.ValueAfter,
                Changed = result.Changed,
                Skipped = result.Skipped,
                MatchesRequestedValue = result.Success,
                SkipReason = result.SkipReason
            };

            await _sessionManager.TouchAsync(sessionId, runtimeSession.Page.Url, cancellationToken);
            return new BrowserActionResult
            {
                Success = result.Success,
                Action = BrowserActionType.Type,
                Message = result.Success
                    ? "Select completed: option value verified."
                    : $"Select failed: {result.Error ?? "target option did not match."}",
                SessionId = sessionId,
                Url = runtimeSession.Page.Url,
                Effect = effect,
                IsRecoverableFailure = false
            };
        }
        catch (Exception ex)
        {
            return BrowserActionFailure(BrowserActionType.Type, sessionId, $"Select failed: {ex.Message}");
        }
    }

    public async Task<BrowserActionResult> SetCheckedAsync(string sessionId, string elementId, bool isChecked, CancellationToken cancellationToken = default)
    {
        try
        {
            var runtimeSession = GetRuntimeSession(sessionId);
            if (!runtimeSession.Elements.TryGetValue(elementId, out var element))
            {
                return BrowserActionFailure(BrowserActionType.Click, sessionId, $"Element not found in latest observation: {elementId}");
            }

            var box = element.BoundingBox;
            var result = await runtimeSession.Page.EvaluateAsync<CheckedControlResult>(SetCheckedElementScript, new
            {
                x = box.X + box.Width / 2,
                y = box.Y + box.Height / 2,
                isChecked
            });

            if (result == null)
            {
                return BrowserActionFailure(BrowserActionType.Click, sessionId, "Set checked failed: no result was returned.");
            }

            var effect = new BrowserActionEffect
            {
                ElementId = element.ElementId,
                TargetStableKey = element.StableKey,
                TargetSelector = element.Selector,
                RequestedText = isChecked ? "checked" : "unchecked",
                ValueBefore = result.CheckedBefore?.ToString(),
                ValueAfter = result.CheckedAfter?.ToString(),
                Changed = result.Changed,
                Skipped = result.Skipped,
                MatchesRequestedValue = result.Success,
                SkipReason = result.SkipReason
            };

            await _sessionManager.TouchAsync(sessionId, runtimeSession.Page.Url, cancellationToken);
            return new BrowserActionResult
            {
                Success = result.Success,
                Action = BrowserActionType.Click,
                Message = result.Success
                    ? "Set checked completed: checked state verified."
                    : $"Set checked failed: {result.Error ?? "checked state did not match."}",
                SessionId = sessionId,
                Url = runtimeSession.Page.Url,
                Effect = effect
            };
        }
        catch (Exception ex)
        {
            return BrowserActionFailure(BrowserActionType.Click, sessionId, $"Set checked failed: {ex.Message}");
        }
    }

    public async Task<BrowserActionResult> UploadAsync(string sessionId, string elementId, string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var runtimeSession = GetRuntimeSession(sessionId);
            if (!runtimeSession.Elements.TryGetValue(elementId, out var element))
            {
                return BrowserActionFailure(BrowserActionType.Upload, sessionId, $"Element not found in latest observation: {elementId}");
            }

            if (string.IsNullOrWhiteSpace(filePath))
            {
                return BrowserActionFailure(BrowserActionType.Upload, sessionId, "Upload failed: file path is required.");
            }

            var absolutePath = Path.GetFullPath(filePath);
            if (!File.Exists(absolutePath))
            {
                return BrowserActionFailure(BrowserActionType.Upload, sessionId, $"Upload failed: file does not exist: {absolutePath}");
            }

            var fileName = Path.GetFileName(absolutePath);
            var box = element.BoundingBox;
            var centerX = box.X + box.Width / 2;
            var centerY = box.Y + box.Height / 2;

            await using var handle = await runtimeSession.Page.EvaluateHandleAsync(FindFileInputElementScript, new
            {
                x = centerX,
                y = centerY
            });

            var inputHandle = handle.AsElement();
            if (inputHandle == null)
            {
                return BrowserActionFailure(BrowserActionType.Upload, sessionId, "Upload failed: no file input target found at the selected element.");
            }

            var before = await inputHandle.EvaluateAsync<FileInputState>(FileInputStateScript);
            await inputHandle.SetInputFilesAsync(absolutePath);
            var after = await inputHandle.EvaluateAsync<FileInputState>(FileInputStateScript);
            var matches = after.FileNames.Any(name => string.Equals(name, fileName, StringComparison.Ordinal));

            var effect = new BrowserActionEffect
            {
                ElementId = element.ElementId,
                TargetStableKey = element.StableKey,
                TargetSelector = element.Selector,
                RequestedText = fileName,
                ValueBefore = string.Join(", ", before.FileNames),
                ValueAfter = string.Join(", ", after.FileNames),
                Changed = !before.FileNames.SequenceEqual(after.FileNames, StringComparer.Ordinal),
                Skipped = false,
                MatchesRequestedValue = matches,
                SkipReason = null
            };

            await _sessionManager.TouchAsync(sessionId, runtimeSession.Page.Url, cancellationToken);
            if (!matches)
            {
                return new BrowserActionResult
                {
                    Success = false,
                    Action = BrowserActionType.Upload,
                    Message = "Upload failed: selected file was not reflected by the file input.",
                    SessionId = sessionId,
                    Url = runtimeSession.Page.Url,
                    Effect = effect,
                    Risk = BrowserRiskType.Upload
                };
            }

            return new BrowserActionResult
            {
                Success = true,
                Action = BrowserActionType.Upload,
                Message = $"Upload completed: selected file `{fileName}`.",
                SessionId = sessionId,
                Url = runtimeSession.Page.Url,
                Effect = effect,
                Risk = BrowserRiskType.Upload
            };
        }
        catch (Exception ex)
        {
            return BrowserActionFailure(BrowserActionType.Upload, sessionId, $"Upload failed: {ex.Message}");
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
            .Where(e => GetBool(e, "isVisible") && GetBool(e, "isEnabled") && GetBool(e, "isTopMost") && GetDouble(e, "width") > 0 && GetDouble(e, "height") > 0)
            .Where(e => GetDouble(e, "x") < viewportWidth && GetDouble(e, "y") < viewportHeight && GetDouble(e, "x") + GetDouble(e, "width") > 0 && GetDouble(e, "y") + GetDouble(e, "height") > 0)
            .OrderByDescending(e => GetDouble(e, "priority"))
            .ThenBy(e => GetDouble(e, "y"))
            .ThenBy(e => GetDouble(e, "x"))
            .Take(maxElements)
            .Select((e, index) => new SomElement
            {
                Index = index + 1,
                ElementId = $"som-{index + 1}",
                StableKey = GetString(e, "stableKey"),
                TagName = GetString(e, "tagName") ?? string.Empty,
                Role = GetString(e, "role"),
                Text = includeText ? Truncate(GetString(e, "text"), 120) : null,
                AriaLabel = includeText ? Truncate(GetString(e, "ariaLabel"), 120) : null,
                Placeholder = includeText ? Truncate(GetString(e, "placeholder"), 120) : null,
                Href = Truncate(GetString(e, "href"), 240),
                Value = includeText ? Truncate(GetString(e, "value"), 120) : null,
                InputType = GetString(e, "inputType"),
                Selector = GetString(e, "selector"),
                IsVisible = GetBool(e, "isVisible"),
                IsEnabled = GetBool(e, "isEnabled"),
                IsEditable = GetBool(e, "isEditable"),
                IsChecked = GetNullableBool(e, "isChecked"),
                IsSensitive = GetBool(e, "isSensitive"),
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

    private static bool? GetNullableBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var result) => result,
            _ => null
        };
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string? DescribeElement(SomElement element)
    {
        var text = FirstNonWhiteSpace(element.Text, element.AriaLabel, element.Placeholder, element.Value, element.InputType, element.Role, element.TagName);
        return Truncate(text, 120);
    }

    private static string? FirstNonWhiteSpace(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private const string FillEditableElementScript = """
        ({ x, y, text }) => {
          const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();

          const deepElementFromPoint = (pointX, pointY) => {
            let element = document.elementFromPoint(pointX, pointY);
            let depth = 0;
            while (element && element.shadowRoot && depth < 8) {
              const inner = element.shadowRoot.elementFromPoint(pointX, pointY);
              if (!inner || inner === element) break;
              element = inner;
              depth += 1;
            }

            return element;
          };

          const parentOrHost = (node) => {
            if (!node) return null;
            if (node.parentElement) return node.parentElement;

            const root = node.getRootNode && node.getRootNode();
            return root && root.host ? root.host : null;
          };

          const inputType = (element) => {
            const tagName = (element.tagName || '').toLowerCase();
            if (tagName === 'input') return (element.getAttribute('type') || 'text').toLowerCase();
            if (tagName === 'textarea') return 'textarea';
            if (tagName === 'select') return 'select';
            if (element.isContentEditable) return 'contenteditable';
            return '';
          };

          const isSensitive = (element) => inputType(element) === 'password';

          const isTextInputType = (type) => [
            '',
            'text',
            'search',
            'email',
            'url',
            'tel',
            'password',
            'number',
            'color',
            'range',
            'date',
            'datetime-local',
            'month',
            'time',
            'week'
          ].includes(type);

          const isEditable = (element) => {
            if (!element || !element.tagName) return false;
            const tagName = element.tagName.toLowerCase();
            const type = inputType(element);
            return tagName === 'textarea' ||
              tagName === 'select' ||
              element.isContentEditable ||
              (tagName === 'input' && isTextInputType(type));
          };

          const findEditable = (start) => {
            let node = start;
            while (node) {
              if (isEditable(node)) return node;
              if (node instanceof HTMLLabelElement && node.control && isEditable(node.control)) return node.control;
              node = parentOrHost(node);
            }

            return null;
          };

          const readActualValue = (element) => {
            const tagName = element.tagName.toLowerCase();
            if (tagName === 'input' || tagName === 'textarea' || tagName === 'select') return element.value || '';
            if (element.isContentEditable) return element.innerText || element.textContent || '';
            return '';
          };

          const publicValue = (element, value) => isSensitive(element) ? null : normalize(value);

          const dispatchValueEvents = (element) => {
            element.dispatchEvent(new InputEvent('input', { bubbles: true, composed: true, inputType: 'insertReplacementText', data: text }));
            element.dispatchEvent(new Event('change', { bubbles: true, composed: true }));
          };

          const setNativeValue = (element, value) => {
            const prototype = Object.getPrototypeOf(element);
            const descriptor = prototype ? Object.getOwnPropertyDescriptor(prototype, 'value') : null;
            if (descriptor && descriptor.set) {
              descriptor.set.call(element, value);
            } else {
              element.value = value;
            }
          };

          const hit = deepElementFromPoint(x, y);
          const target = findEditable(hit);
          if (!target) {
            return {
              Success: false,
              Error: 'No editable target found at the selected element.',
              ValueBefore: null,
              ValueAfter: null,
              Changed: false,
              Skipped: false,
              MatchesRequestedValue: false,
              SkipReason: null,
              IsSensitive: false
            };
          }

          const sensitive = isSensitive(target);
          const beforeActual = readActualValue(target);
          if (beforeActual === text) {
            return {
              Success: true,
              Error: null,
              ValueBefore: publicValue(target, beforeActual),
              ValueAfter: publicValue(target, beforeActual),
              Changed: false,
              Skipped: true,
              MatchesRequestedValue: true,
              SkipReason: 'already-matches-requested-value',
              IsSensitive: sensitive
            };
          }

          const tagName = target.tagName.toLowerCase();
          target.focus();
          if (tagName === 'select') {
            const option = Array.from(target.options || []).find(item => item.value === text || normalize(item.textContent || '') === normalize(text));
            if (!option) {
              return {
                Success: false,
                Error: 'No select option matches the requested text.',
                ValueBefore: publicValue(target, beforeActual),
                ValueAfter: publicValue(target, beforeActual),
                Changed: false,
                Skipped: false,
                MatchesRequestedValue: false,
                SkipReason: null,
                IsSensitive: sensitive
              };
            }

            target.value = option.value;
            dispatchValueEvents(target);
          } else if (target.isContentEditable) {
            target.textContent = text;
            dispatchValueEvents(target);
          } else {
            setNativeValue(target, text);
            dispatchValueEvents(target);
          }

          const afterActual = readActualValue(target);
          const matches = afterActual === text || (tagName === 'select' && normalize(target.selectedOptions?.[0]?.textContent || '') === normalize(text));
          return {
            Success: matches,
            Error: matches ? null : 'Target value did not match requested text after fill.',
            ValueBefore: publicValue(target, beforeActual),
            ValueAfter: publicValue(target, afterActual),
            Changed: beforeActual !== afterActual,
            Skipped: false,
            MatchesRequestedValue: matches,
            SkipReason: null,
            IsSensitive: sensitive
          };
        }
        """;

    private const string SelectElementOptionScript = """
        ({ x, y, text }) => {
          const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();

          const deepElementFromPoint = (pointX, pointY) => {
            let element = document.elementFromPoint(pointX, pointY);
            let depth = 0;
            while (element && element.shadowRoot && depth < 8) {
              const inner = element.shadowRoot.elementFromPoint(pointX, pointY);
              if (!inner || inner === element) break;
              element = inner;
              depth += 1;
            }

            return element;
          };

          const parentOrHost = (node) => {
            if (!node) return null;
            if (node.parentElement) return node.parentElement;
            const root = node.getRootNode && node.getRootNode();
            return root && root.host ? root.host : null;
          };

          const isSelect = (element) => element && element.tagName && element.tagName.toLowerCase() === 'select';

          const findSelect = (start) => {
            let node = start;
            while (node) {
              if (isSelect(node)) return node;
              if (node instanceof HTMLLabelElement && isSelect(node.control)) return node.control;
              if (node.querySelector) {
                const nested = node.querySelector('select');
                if (nested) return nested;
              }
              node = parentOrHost(node);
            }

            return null;
          };

          const select = findSelect(deepElementFromPoint(x, y));
          if (!select) {
            return {
              Success: false,
              Error: 'No native select target found at the selected element.',
              ValueBefore: null,
              ValueAfter: null,
              SelectedText: null,
              Changed: false,
              Skipped: false,
              SkipReason: null
            };
          }

          const beforeValue = select.value || '';
          const beforeText = normalize(select.selectedOptions?.[0]?.textContent || '');
          const requested = normalize(text);
          let option = Array.from(select.options || []).find(item => !item.disabled && (item.value === text || normalize(item.textContent || '') === requested));
          if (!option && !requested) {
            option = Array.from(select.options || []).find(item => !item.disabled && item.value !== beforeValue) ||
              Array.from(select.options || []).find(item => !item.disabled);
          }

          if (!option) {
            return {
              Success: false,
              Error: 'No select option matches the requested text.',
              ValueBefore: beforeText || beforeValue,
              ValueAfter: beforeText || beforeValue,
              SelectedText: null,
              Changed: false,
              Skipped: false,
              SkipReason: null
            };
          }

          const selectedText = normalize(option.textContent || '');
          if (select.value === option.value) {
            return {
              Success: true,
              Error: null,
              ValueBefore: beforeText || beforeValue,
              ValueAfter: selectedText || option.value,
              SelectedText: selectedText || option.value,
              Changed: false,
              Skipped: true,
              SkipReason: 'already-selected'
            };
          }

          select.value = option.value;
          select.dispatchEvent(new Event('input', { bubbles: true, composed: true }));
          select.dispatchEvent(new Event('change', { bubbles: true, composed: true }));
          const afterValue = select.value || '';
          const afterText = normalize(select.selectedOptions?.[0]?.textContent || '');
          const matches = afterValue === option.value || afterText === selectedText;
          return {
            Success: matches,
            Error: matches ? null : 'Selected option did not match after change.',
            ValueBefore: beforeText || beforeValue,
            ValueAfter: afterText || afterValue,
            SelectedText: selectedText || option.value,
            Changed: beforeValue !== afterValue,
            Skipped: false,
            SkipReason: null
          };
        }
        """;

    private const string SetCheckedElementScript = """
        ({ x, y, isChecked }) => {
          const deepElementFromPoint = (pointX, pointY) => {
            let element = document.elementFromPoint(pointX, pointY);
            let depth = 0;
            while (element && element.shadowRoot && depth < 8) {
              const inner = element.shadowRoot.elementFromPoint(pointX, pointY);
              if (!inner || inner === element) break;
              element = inner;
              depth += 1;
            }

            return element;
          };

          const parentOrHost = (node) => {
            if (!node) return null;
            if (node.parentElement) return node.parentElement;
            const root = node.getRootNode && node.getRootNode();
            return root && root.host ? root.host : null;
          };

          const role = (element) => (element?.getAttribute?.('role') || '').toLowerCase();
          const inputType = (element) => element?.tagName?.toLowerCase() === 'input'
            ? (element.getAttribute('type') || 'text').toLowerCase()
            : '';
          const isNativeCheckable = (element) => element?.tagName?.toLowerCase() === 'input' && ['checkbox', 'radio'].includes(inputType(element));
          const isAriaCheckable = (element) => ['checkbox', 'radio'].includes(role(element)) || element?.hasAttribute?.('aria-checked');
          const readChecked = (element) => {
            if (!element) return null;
            if (isNativeCheckable(element)) return !!element.checked;
            const value = (element.getAttribute('aria-checked') || '').toLowerCase();
            if (value === 'true') return true;
            if (value === 'false') return false;
            return null;
          };

          const findCheckable = (start) => {
            let node = start;
            while (node) {
              if (isNativeCheckable(node) || isAriaCheckable(node)) return { target: node, clickTarget: node };
              if (node instanceof HTMLLabelElement && isNativeCheckable(node.control)) return { target: node.control, clickTarget: node };
              const label = node.closest && node.closest('label');
              if (label instanceof HTMLLabelElement && isNativeCheckable(label.control)) return { target: label.control, clickTarget: label };
              if (node.querySelector) {
                const nested = Array.from(node.querySelectorAll('input')).find(isNativeCheckable) ||
                  Array.from(node.querySelectorAll('[role="checkbox"],[role="radio"],[aria-checked]')).find(isAriaCheckable);
                if (nested) return { target: nested, clickTarget: node };
              }
              node = parentOrHost(node);
            }

            return null;
          };

          const found = findCheckable(deepElementFromPoint(x, y));
          if (!found) {
            return {
              Success: false,
              Error: 'No checkbox or radio target found at the selected element.',
              CheckedBefore: null,
              CheckedAfter: null,
              Changed: false,
              Skipped: false,
              SkipReason: null
            };
          }

          const before = readChecked(found.target);
          if (before === isChecked) {
            return {
              Success: true,
              Error: null,
              CheckedBefore: before,
              CheckedAfter: before,
              Changed: false,
              Skipped: true,
              SkipReason: 'already-matches-requested-state'
            };
          }

          if (inputType(found.target) === 'radio' && isChecked === false) {
            return {
              Success: false,
              Error: 'Radio controls cannot be unchecked directly.',
              CheckedBefore: before,
              CheckedAfter: before,
              Changed: false,
              Skipped: false,
              SkipReason: null
            };
          }

          found.clickTarget.click();
          const after = readChecked(found.target);
          const success = after === isChecked;
          return {
            Success: success,
            Error: success ? null : 'Checked state did not match after click.',
            CheckedBefore: before,
            CheckedAfter: after,
            Changed: before !== after,
            Skipped: false,
            SkipReason: null
          };
        }
        """;

    private const string FindFileInputElementScript = """
        ({ x, y }) => {
          const deepElementFromPoint = (pointX, pointY) => {
            let element = document.elementFromPoint(pointX, pointY);
            let depth = 0;
            while (element && element.shadowRoot && depth < 8) {
              const inner = element.shadowRoot.elementFromPoint(pointX, pointY);
              if (!inner || inner === element) break;
              element = inner;
              depth += 1;
            }

            return element;
          };

          const parentOrHost = (node) => {
            if (!node) return null;
            if (node.parentElement) return node.parentElement;

            const root = node.getRootNode && node.getRootNode();
            return root && root.host ? root.host : null;
          };

          const isFileInput = (element) => {
            return element &&
              element.tagName &&
              element.tagName.toLowerCase() === 'input' &&
              (element.getAttribute('type') || 'text').toLowerCase() === 'file';
          };

          const labelControl = (element) => {
            if (!element) return null;
            if (element instanceof HTMLLabelElement && isFileInput(element.control)) return element.control;

            const label = element.closest && element.closest('label');
            if (label instanceof HTMLLabelElement && isFileInput(label.control)) return label.control;

            return null;
          };

          const descendantFileInput = (element) => {
            if (!element || !element.querySelector) return null;
            return Array.from(element.querySelectorAll('input')).find(isFileInput) || null;
          };

          let node = deepElementFromPoint(x, y);
          while (node) {
            if (isFileInput(node)) return node;

            const controlled = labelControl(node);
            if (controlled) return controlled;

            const descendant = descendantFileInput(node);
            if (descendant) return descendant;

            node = parentOrHost(node);
          }

          return null;
        }
        """;

    private const string FileInputStateScript = """
        input => ({
          FileNames: Array.from(input.files || []).map(file => file.name),
          FileCount: input.files ? input.files.length : 0,
          Value: input.value || '',
          Multiple: !!input.multiple
        })
        """;

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
            '[onclick]',
            'label'
          ].join(',');

          const modalSelectors = [
            'dialog[open]',
            ':modal',
            ':popover-open',
            '[role="dialog"]',
            '[role="alertdialog"]',
            '[aria-modal="true"]',
            '[data-modal]',
            '[data-dialog]',
            '.modal',
            '.modal-dialog',
            '.modal-content',
            '.popover',
            '.popup',
            '.overlay',
            '.drawer'
          ];

          const safeMatches = (el, selector) => {
            try {
              return !!el.matches && el.matches(selector);
            } catch {
              return false;
            }
          };

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
              if (parent) {
                node = parent;
                continue;
              }

              const root = node.getRootNode && node.getRootNode();
              if (root && root.host) {
                parts.unshift(':shadow-root');
                node = root.host;
                continue;
              }

              node = null;
            }
            return parts.join(' > ');
          };

          const collectElements = (root, output = []) => {
            if (!root || !root.querySelectorAll) return output;

            for (const el of Array.from(root.querySelectorAll('*'))) {
              output.push(el);
              if (el.shadowRoot) {
                collectElements(el.shadowRoot, output);
              }
            }

            return output;
          };

          const isVisible = (el, rect) => {
            const style = window.getComputedStyle(el);
            return style &&
              style.visibility !== 'hidden' &&
              style.display !== 'none' &&
              Number(style.opacity || '1') > 0 &&
              style.pointerEvents !== 'none' &&
              rect.width > 0 &&
              rect.height > 0;
          };

          const isEnabled = (el) => {
            return !el.disabled && el.getAttribute('aria-disabled') !== 'true';
          };

          const getInputType = (el) => {
            const tagName = (el.tagName || '').toLowerCase();
            if (tagName === 'input') return (el.getAttribute('type') || 'text').toLowerCase();
            if (tagName === 'textarea') return 'textarea';
            if (tagName === 'select') return 'select';
            if (el.isContentEditable) return 'contenteditable';
            return '';
          };

          const isTextInputType = (type) => [
            '',
            'text',
            'search',
            'email',
            'url',
            'tel',
            'password',
            'number',
            'color',
            'range',
            'date',
            'datetime-local',
            'month',
            'time',
            'week'
          ].includes(type);

          const isEditableElement = (el) => {
            const tagName = (el.tagName || '').toLowerCase();
            const role = (el.getAttribute('role') || '').toLowerCase();
            const inputType = getInputType(el);
            return (tagName === 'input' && isTextInputType(inputType)) ||
              tagName === 'textarea' ||
              tagName === 'select' ||
              el.isContentEditable ||
              role === 'textbox' ||
              role === 'searchbox' ||
              role === 'combobox';
          };

          const isSensitiveElement = (el) => {
            const inputType = getInputType(el);
            return inputType === 'password';
          };

          const elementValue = (el) => {
            if (isSensitiveElement(el)) return null;

            const tagName = (el.tagName || '').toLowerCase();
            const inputType = getInputType(el);
            if (tagName === 'input' && (inputType === 'checkbox' || inputType === 'radio')) {
              return el.checked ? 'checked' : 'unchecked';
            }

            if (tagName === 'input' && inputType === 'file') {
              return normalize(Array.from(el.files || []).map(file => file.name).join(', '));
            }

            if (tagName === 'input' || tagName === 'textarea' || tagName === 'select') {
              return normalize(el.value || '');
            }

            if (el.isContentEditable) {
              return normalize(el.innerText || el.textContent || '');
            }

            return null;
          };

          const elementText = (el) => {
            if (isSensitiveElement(el)) return '';

            const editableValue = elementValue(el);
            return normalize(el.innerText || el.textContent || editableValue || '');
          };

          const isFileInput = (el) => {
            return el &&
              (el.tagName || '').toLowerCase() === 'input' &&
              getInputType(el) === 'file';
          };

          const isCheckableInput = (el) => {
            const inputType = getInputType(el);
            return el &&
              (el.tagName || '').toLowerCase() === 'input' &&
              (inputType === 'checkbox' || inputType === 'radio');
          };

          const isNativeSelect = (el) => {
            return el && (el.tagName || '').toLowerCase() === 'select';
          };

          const isAssociatedControl = (el) => isFileInput(el) || isCheckableInput(el) || isNativeSelect(el);

          const associatedControl = (el) => {
            if (!el) return null;
            if (isAssociatedControl(el)) return el;
            if (el instanceof HTMLLabelElement && isAssociatedControl(el.control)) return el.control;

            const label = el.closest && el.closest('label');
            if (label instanceof HTMLLabelElement && isAssociatedControl(label.control)) return label.control;

            if (el.querySelector) {
              const nested = Array.from(el.querySelectorAll('input,select')).find(isAssociatedControl);
              if (nested) return nested;
            }

            return null;
          };

          const stableKey = (el, selectorPath, stateElement = el) => {
            const parts = [
              selectorPath,
              (el.tagName || '').toLowerCase(),
              stateElement.getAttribute('role') || el.getAttribute('role') || '',
              stateElement.getAttribute('name') || el.getAttribute('name') || '',
              getInputType(stateElement),
              stateElement.getAttribute('aria-label') || el.getAttribute('aria-label') || '',
              stateElement.getAttribute('placeholder') || el.getAttribute('placeholder') || ''
            ].map(value => normalize(value)).filter(Boolean);

            return parts.join('|').slice(0, 512);
          };

          const normalizeClassName = (el) => {
            if (!el || !el.className) return '';
            return typeof el.className === 'string' ? el.className : String(el.className.baseVal || '');
          };

          const looksLikeModalRoot = (el) => {
            if (modalSelectors.some(modalSelector => safeMatches(el, modalSelector))) return true;

            const className = normalizeClassName(el);
            const id = el.id || '';
            return /\b(modal|dialog|popup|popover|overlay|drawer)\b/i.test(`${id} ${className}`);
          };

          const blocksBackground = (el) => {
            const role = (el.getAttribute('role') || '').toLowerCase();
            return safeMatches(el, ':modal') ||
              el.getAttribute('aria-modal') === 'true' ||
              role === 'alertdialog';
          };

          const composedContains = (container, child) => {
            let node = child;
            while (node) {
              if (node === container) return true;
              if (container.contains && container.contains(node)) return true;

              if (node.parentElement) {
                node = node.parentElement;
                continue;
              }

              const root = node.getRootNode && node.getRootNode();
              node = root && root.host ? root.host : null;
            }

            return false;
          };

          const isLabelRelated = (hit, target) => {
            if (!hit || !target) return false;
            if (hit instanceof HTMLLabelElement && hit.control === target) return true;

            if (!target.id || !hit.closest) return false;
            try {
              const label = hit.closest(`label[for="${CSS.escape(target.id)}"]`);
              return !!label;
            } catch {
              return false;
            }
          };

          const isRelatedHit = (hit, target, shadowHost) => {
            if (!hit || !target) return false;
            return hit === target ||
              target.contains(hit) ||
              isLabelRelated(hit, target) ||
              (!!shadowHost && (hit === shadowHost || shadowHost.contains(hit)));
          };

          const firstRelevantHitMatches = (stack, target, shadowHost) => {
            for (const hit of stack || []) {
              if (hit === document.documentElement || hit === document.body) continue;
              return isRelatedHit(hit, target, shadowHost);
            }

            return false;
          };

          const pointHitsElement = (el, x, y) => {
            if (x < 0 || y < 0 || x > window.innerWidth || y > window.innerHeight) return false;

            const root = el.getRootNode && el.getRootNode();
            const shadowHost = root && root.host ? root.host : null;
            const documentStack = document.elementsFromPoint(x, y);
            if (!firstRelevantHitMatches(documentStack, shadowHost || el, shadowHost) &&
                !firstRelevantHitMatches(documentStack, el, shadowHost)) {
              return false;
            }

            if (root && root !== document && root.elementsFromPoint) {
              return firstRelevantHitMatches(root.elementsFromPoint(x, y), el, null);
            }

            return true;
          };

          const samplePoints = (rect) => {
            const left = Math.max(0, rect.left);
            const right = Math.min(window.innerWidth, rect.right);
            const top = Math.max(0, rect.top);
            const bottom = Math.min(window.innerHeight, rect.bottom);
            const width = Math.max(0, right - left);
            const height = Math.max(0, bottom - top);
            if (width <= 0 || height <= 0) return [];

            const points = [
              [left + width / 2, top + height / 2],
              [left + width * 0.25, top + height * 0.25],
              [left + width * 0.75, top + height * 0.25],
              [left + width * 0.25, top + height * 0.75],
              [left + width * 0.75, top + height * 0.75]
            ];

            const seenPoints = new Set();
            return points.filter(([x, y]) => {
              const key = `${Math.round(x)}:${Math.round(y)}`;
              if (seenPoints.has(key)) return false;
              seenPoints.add(key);
              return true;
            });
          };

          const topMostInfo = (el, rect) => {
            const points = samplePoints(rect);
            if (points.length === 0) return { center: false, score: 0 };

            let score = 0;
            let center = false;
            points.forEach(([x, y], index) => {
              const hit = pointHitsElement(el, x, y);
              if (hit) score += 1;
              if (index === 0) center = hit;
            });

            return { center, score };
          };

          const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();
          const allElements = collectElements(document);
          const modalRoots = allElements.filter(el => {
            const rect = el.getBoundingClientRect();
            return looksLikeModalRoot(el) && isVisible(el, rect);
          });
          const blockingModalRoots = modalRoots.filter(blocksBackground);
          const elements = [];
          const seen = new Set();

          for (const el of allElements.filter(item => safeMatches(item, selector))) {
            if (seen.has(el)) continue;
            seen.add(el);

            const rect = el.getBoundingClientRect();
            if (!isVisible(el, rect)) continue;

            const tagName = (el.tagName || '').toLowerCase();
            const stateControl = associatedControl(el);
            if (tagName === 'label' && !stateControl) continue;

            const inBlockingModal = blockingModalRoots.some(root => composedContains(root, el));
            if (blockingModalRoots.length > 0 && !inBlockingModal) continue;

            const hitInfo = topMostInfo(el, rect);
            const isTopMost = hitInfo.center || hitInfo.score >= Math.min(2, samplePoints(rect).length);
            if (!isTopMost) continue;

            const modalDepth = modalRoots.filter(root => composedContains(root, el)).length;
            const priority = (inBlockingModal ? 3000 : 0) + (modalDepth * 100) + hitInfo.score;
            const selectorPath = cssPath(el);
            const stateElement = stateControl || el;
            const inputType = getInputType(stateElement);
            const isEditable = isEditableElement(stateElement);
            const sensitive = isSensitiveElement(stateElement);
            const value = elementValue(stateElement);
            const isCheckable = inputType === 'checkbox' || inputType === 'radio';

            elements.push({
              tagName,
              role: stateElement.getAttribute('role') || el.getAttribute('role') || '',
              text: elementText(el),
              ariaLabel: normalize(stateElement.getAttribute('aria-label') || el.getAttribute('aria-label') || ''),
              placeholder: normalize(stateElement.getAttribute('placeholder') || el.getAttribute('placeholder') || ''),
              href: el.href || el.getAttribute('href') || '',
              selector: selectorPath,
              stableKey: stableKey(el, selectorPath, stateElement),
              value,
              inputType,
              x: rect.x,
              y: rect.y,
              width: rect.width,
              height: rect.height,
              isVisible: true,
              isEnabled: isEnabled(stateElement),
              isEditable,
              isChecked: isCheckable ? !!stateElement.checked : null,
              isSensitive: sensitive,
              isTopMost: true,
              priority
            });
          }

          return elements;
        }
        """;

    private sealed record RuntimeSession(IBrowserContext Context, IPage Page, BrowserSessionOptions Options)
    {
        public ConcurrentDictionary<string, SomElement> Elements { get; } = new();
    }

    private sealed class FileInputState
    {
        public List<string> FileNames { get; set; } = new();
        public int FileCount { get; set; }
        public string? Value { get; set; }
        public bool Multiple { get; set; }
    }

    private sealed class SelectControlResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string? ValueBefore { get; set; }
        public string? ValueAfter { get; set; }
        public string? SelectedText { get; set; }
        public bool Changed { get; set; }
        public bool Skipped { get; set; }
        public string? SkipReason { get; set; }
    }

    private sealed class CheckedControlResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public bool? CheckedBefore { get; set; }
        public bool? CheckedAfter { get; set; }
        public bool Changed { get; set; }
        public bool Skipped { get; set; }
        public string? SkipReason { get; set; }
    }

    private sealed class TypeFillResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string? ValueBefore { get; set; }
        public string? ValueAfter { get; set; }
        public bool Changed { get; set; }
        public bool Skipped { get; set; }
        public bool MatchesRequestedValue { get; set; }
        public string? SkipReason { get; set; }
        public bool IsSensitive { get; set; }
    }

    private sealed class ScrollPosition
    {
        public int X { get; set; }
        public int Y { get; set; }
    }
}
