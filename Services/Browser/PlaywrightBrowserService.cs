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
        var storageStatePath = options.PersistSession ? GetPersistentStorageStatePath() : null;
        var contextOptions = new BrowserNewContextOptions
        {
            AcceptDownloads = options.DownloadEnabled,
            ViewportSize = new ViewportSize
            {
                Width = Math.Max(320, options.Viewport.Width),
                Height = Math.Max(240, options.Viewport.Height)
            },
            DeviceScaleFactor = (float)options.Viewport.DeviceScaleFactor
        };

        if (!string.IsNullOrWhiteSpace(storageStatePath) && File.Exists(storageStatePath))
        {
            contextOptions.StorageStatePath = storageStatePath;
        }

        var context = await browser.NewContextAsync(contextOptions);

        context.SetDefaultTimeout(options.OperationTimeoutSeconds * 1000);
        context.SetDefaultNavigationTimeout(options.OperationTimeoutSeconds * 1000);

        var page = await context.NewPageAsync();
        _runtimeSessions[session.SessionId] = new RuntimeSession(context, page, options, storageStatePath);

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

    public async Task<SomObservation> ObserveWithoutElementsAsync(string sessionId, string? errorMessage = null, CancellationToken cancellationToken = default)
    {
        var runtimeSession = GetRuntimeSession(sessionId);
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

        var observation = await _somAnnotator.AnnotateAsync(new SomAnnotationRequest
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
            DrawAnnotations = false,
            ScreenshotScale = runtimeSession.Options.ScreenshotScale,
            ImageQuality = runtimeSession.Options.ImageQuality,
            Elements = new List<SomElement>()
        }, cancellationToken);

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            runtimeSession.LastObservationFallbackReason = errorMessage;
        }

        await _sessionManager.TouchAsync(sessionId, runtimeSession.Page.Url, cancellationToken);
        return observation;
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
                    Effect = effect,
                    IsRecoverableFailure = true
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
                IsRecoverableFailure = !result.Success
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
                Effect = effect,
                IsRecoverableFailure = !result.Success
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
                    Risk = BrowserRiskType.Upload,
                    IsRecoverableFailure = true
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

    public async Task<BrowserActionResult> SavePdfAsync(string sessionId, string? fileName = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var runtimeSession = GetRuntimeSession(sessionId);
            var title = await runtimeSession.Page.TitleAsync();
            var resolvedFileName = SanitizeArtifactFileName(fileName, string.IsNullOrWhiteSpace(title) ? "page" : title, ".pdf");
            var directory = GetArtifactDirectory(sessionId);
            Directory.CreateDirectory(directory);
            var path = GetUniqueArtifactPath(directory, resolvedFileName);

            await runtimeSession.Page.PdfAsync(new PagePdfOptions
            {
                Path = path,
                PrintBackground = true
            });

            await _sessionManager.TouchAsync(sessionId, runtimeSession.Page.Url, cancellationToken);
            return new BrowserActionResult
            {
                Success = true,
                Action = BrowserActionType.SaveAsPdf,
                Message = $"PDF saved: {path}",
                SessionId = sessionId,
                Url = runtimeSession.Page.Url,
                ExtractedContent = $"PDF saved: {path}",
                LongTermMemory = $"Saved current page as PDF: {path}",
                Attachments = { path },
                Metadata = { ["pdfPath"] = path }
            };
        }
        catch (Exception ex)
        {
            return BrowserActionFailure(BrowserActionType.SaveAsPdf, sessionId, $"Save PDF failed: {ex.Message}");
        }
    }

    public async Task<BrowserActionResult> EvaluateAsync(string sessionId, string code, CancellationToken cancellationToken = default)
    {
        try
        {
            var runtimeSession = GetRuntimeSession(sessionId);
            if (string.IsNullOrWhiteSpace(code))
            {
                return BrowserActionFailure(BrowserActionType.Evaluate, sessionId, "Evaluate failed: code is required.");
            }

            var value = await runtimeSession.Page.EvaluateAsync<JsonElement>(code);
            var text = value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? "null"
                : value.GetRawText();
            if (text.Length > 20000)
            {
                text = text[..20000] + "\n... [truncated]";
            }

            await _sessionManager.TouchAsync(sessionId, runtimeSession.Page.Url, cancellationToken);
            return new BrowserActionResult
            {
                Success = true,
                Action = BrowserActionType.Evaluate,
                Message = "JavaScript evaluated.",
                SessionId = sessionId,
                Url = runtimeSession.Page.Url,
                ExtractedContent = text,
                LongTermMemory = $"JavaScript evaluated. Result length={text.Length}."
            };
        }
        catch (Exception ex)
        {
            return BrowserActionFailure(BrowserActionType.Evaluate, sessionId, $"Evaluate failed: {ex.Message}");
        }
    }

    public async Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (_runtimeSessions.TryRemove(sessionId, out var runtimeSession))
        {
            try
            {
                if (runtimeSession.Options.PersistSession && !string.IsNullOrWhiteSpace(runtimeSession.StorageStatePath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(runtimeSession.StorageStatePath)!);
                    await runtimeSession.Context.StorageStateAsync(new BrowserContextStorageStateOptions
                    {
                        Path = runtimeSession.StorageStatePath
                    });
                }

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

    private static string GetPersistentStorageStatePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = AppContext.BaseDirectory;
        }

        return Path.Combine(root, "Athena", "Browser", "persistent-storage-state.json");
    }

    private static string GetArtifactDirectory(string sessionId)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = AppContext.BaseDirectory;
        }

        return Path.Combine(root, "Athena", "Browser", "Artifacts", SanitizePathSegment(sessionId));
    }

    private static string SanitizeArtifactFileName(string? requestedName, string fallbackName, string extension)
    {
        var raw = string.IsNullOrWhiteSpace(requestedName) ? fallbackName : requestedName;
        var sanitized = SanitizePathSegment(Path.GetFileNameWithoutExtension(raw));
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "artifact";
        }

        return sanitized.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? sanitized
            : sanitized + extension;
    }

    private static string SanitizePathSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "artifact" : sanitized;
    }

    private static string GetUniqueArtifactPath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            return path;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{stem}_{index}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{stem}_{Guid.NewGuid():N}{extension}");
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
            DrawAnnotations = runtimeSession.Options.ObservationMode == BrowserObservationMode.VisionWithSom,
            ScreenshotScale = runtimeSession.Options.ScreenshotScale,
            ImageQuality = runtimeSession.Options.ImageQuality,
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

    private async Task<List<SomElement>> ExtractInteractiveElementsAsync(RuntimeSession runtimeSession, int viewportWidth, int viewportHeight)
    {
        var extractionJson = await runtimeSession.Page.EvaluateAsync<JsonElement>(InteractiveElementsScript);
        var maxElements = Math.Max(0, runtimeSession.Options.SomMaxElements);
        var includeText = runtimeSession.Options.SomIncludeText;
        var candidates = ParseInteractiveElements(extractionJson);

        var filteredCandidates = candidates
            .Where(e => GetBool(e, "isVisible") && GetBool(e, "isEnabled") && GetBool(e, "isTopMost") && GetDouble(e, "width") > 0 && GetDouble(e, "height") > 0)
            .Where(e => GetDouble(e, "x") < viewportWidth && GetDouble(e, "y") < viewportHeight && GetDouble(e, "x") + GetDouble(e, "width") > 0 && GetDouble(e, "y") + GetDouble(e, "height") > 0)
            .OrderByDescending(e => GetDouble(e, "priority"))
            .ThenBy(e => GetDouble(e, "y"))
            .ThenBy(e => GetDouble(e, "x"))
            .Take(maxElements)
            .Select(e => new SomElement
            {
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

        AssignStableElementIds(runtimeSession, filteredCandidates);

        var sameOriginIframes = GetInt(extractionJson, "sameOriginIframeCount");
        var crossOriginIframes = GetInt(extractionJson, "crossOriginIframeCount");
        var prePaintCount = GetInt(extractionJson, "prePaintCandidateCount");
        var postPaintCount = GetInt(extractionJson, "postPaintCandidateCount");
        var reused = filteredCandidates.Count(e => runtimeSession.SeenElementIds.Contains(e.ElementId));
        var reuseRate = filteredCandidates.Count == 0 ? 0.0 : (double)reused / filteredCandidates.Count;
        _logger.Debug(
            "SoM extraction stats: candidates={Candidates}, prePaint={PrePaint}, postPaint={PostPaint}, sameOriginIframes={SameOriginIframes}, crossOriginIframes={CrossOriginIframes}, elementReuseRate={ReuseRate:P1}",
            filteredCandidates.Count, prePaintCount, postPaintCount, sameOriginIframes, crossOriginIframes, reuseRate);

        return filteredCandidates;
    }

    private static List<JsonElement> ParseInteractiveElements(JsonElement json)
    {
        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("elements", out var wrappedElements) && wrappedElements.ValueKind == JsonValueKind.Array)
        {
            json = wrappedElements;
        }

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

    private static int GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var number) => number,
            _ => 0
        };
    }

    private static string BuildMatchKey(SomElement element)
    {
        return string.Join("|", new[]
        {
            element.Selector ?? string.Empty,
            element.Role ?? string.Empty,
            element.InputType ?? string.Empty
        }).ToLowerInvariant();
    }

    private static double ComputeIoU(BrowserBoundingBox a, BrowserBoundingBox b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        var y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        var intersection = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        if (intersection <= 0) return 0;

        var union = (a.Width * a.Height) + (b.Width * b.Height) - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private static void AssignStableElementIds(RuntimeSession runtimeSession, List<SomElement> elements)
    {
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var previous = runtimeSession.LastElements.ToList();

        foreach (var element in elements)
        {
            string? selectedId = null;
            if (!string.IsNullOrWhiteSpace(element.StableKey) && runtimeSession.StableKeyToElementId.TryGetValue(element.StableKey, out var stableId) && !usedIds.Contains(stableId))
            {
                selectedId = stableId;
            }

            if (selectedId == null)
            {
                var matchKey = BuildMatchKey(element);
                var matchedBySignature = previous.FirstOrDefault(p => !usedIds.Contains(p.ElementId) && BuildMatchKey(p) == matchKey);
                if (matchedBySignature != null)
                {
                    selectedId = matchedBySignature.ElementId;
                }
            }

            if (selectedId == null)
            {
                var matchedByIou = previous
                    .Where(p => !usedIds.Contains(p.ElementId))
                    .Select(p => new { p.ElementId, IoU = ComputeIoU(p.BoundingBox, element.BoundingBox) })
                    .OrderByDescending(p => p.IoU)
                    .FirstOrDefault();
                if (matchedByIou != null && matchedByIou.IoU >= 0.65)
                {
                    selectedId = matchedByIou.ElementId;
                }
            }

            if (selectedId == null)
            {
                runtimeSession.NextElementOrdinal++;
                selectedId = $"som-{runtimeSession.NextElementOrdinal}";
            }

            element.ElementId = selectedId;
            usedIds.Add(selectedId);
        }

        for (var i = 0; i < elements.Count; i++)
        {
            elements[i].Index = i + 1;
        }

        runtimeSession.SeenElementIds.Clear();
        foreach (var current in runtimeSession.LastElements)
        {
            runtimeSession.SeenElementIds.Add(current.ElementId);
        }

        runtimeSession.LastElements.Clear();
        runtimeSession.LastElements.AddRange(elements);

        foreach (var element in elements)
        {
            if (!string.IsNullOrWhiteSpace(element.StableKey))
            {
                runtimeSession.StableKeyToElementId[element.StableKey] = element.ElementId;
            }
        }

        var activeIds = new HashSet<string>(elements.Select(e => e.ElementId), StringComparer.OrdinalIgnoreCase);
        var staleKeys = runtimeSession.StableKeyToElementId
            .Where(pair => !activeIds.Contains(pair.Value))
            .Select(pair => pair.Key)
            .Take(Math.Max(0, runtimeSession.StableKeyToElementId.Count - 500))
            .ToList();
        foreach (var key in staleKeys)
        {
            runtimeSession.StableKeyToElementId.Remove(key);
        }
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

          const deepElementFromPoint = (doc, pointX, pointY, depth = 0) => {
            if (!doc || depth > 12 || pointX < 0 || pointY < 0) return null;
            let element = doc.elementFromPoint(pointX, pointY);
            if (!element) return null;

            let shadowDepth = 0;
            while (element && element.shadowRoot && shadowDepth < 8) {
              const inner = element.shadowRoot.elementFromPoint(pointX, pointY);
              if (!inner || inner === element) break;
              element = inner;
              shadowDepth += 1;
            }

            if (element && element.tagName && element.tagName.toLowerCase() === 'iframe') {
              try {
                const frameDoc = element.contentDocument;
                if (frameDoc) {
                  const rect = element.getBoundingClientRect();
                  const nested = deepElementFromPoint(frameDoc, pointX - rect.left, pointY - rect.top, depth + 1);
                  if (nested) return nested;
                }
              } catch {
                // Cross-origin iframe is not accessible by script.
              }
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

          const hit = deepElementFromPoint(document, x, y);
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

          const deepElementFromPoint = (doc, pointX, pointY, depth = 0) => {
            if (!doc || depth > 12 || pointX < 0 || pointY < 0) return null;
            let element = doc.elementFromPoint(pointX, pointY);
            if (!element) return null;
            let shadowDepth = 0;
            while (element && element.shadowRoot && shadowDepth < 8) {
              const inner = element.shadowRoot.elementFromPoint(pointX, pointY);
              if (!inner || inner === element) break;
              element = inner;
              shadowDepth += 1;
            }
            if (element && element.tagName && element.tagName.toLowerCase() === 'iframe') {
              try {
                const frameDoc = element.contentDocument;
                if (frameDoc) {
                  const rect = element.getBoundingClientRect();
                  const nested = deepElementFromPoint(frameDoc, pointX - rect.left, pointY - rect.top, depth + 1);
                  if (nested) return nested;
                }
              } catch {}
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

          const select = findSelect(deepElementFromPoint(document, x, y));
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
          const deepElementFromPoint = (doc, pointX, pointY, depth = 0) => {
            if (!doc || depth > 12 || pointX < 0 || pointY < 0) return null;
            let element = doc.elementFromPoint(pointX, pointY);
            if (!element) return null;
            let shadowDepth = 0;
            while (element && element.shadowRoot && shadowDepth < 8) {
              const inner = element.shadowRoot.elementFromPoint(pointX, pointY);
              if (!inner || inner === element) break;
              element = inner;
              shadowDepth += 1;
            }
            if (element && element.tagName && element.tagName.toLowerCase() === 'iframe') {
              try {
                const frameDoc = element.contentDocument;
                if (frameDoc) {
                  const rect = element.getBoundingClientRect();
                  const nested = deepElementFromPoint(frameDoc, pointX - rect.left, pointY - rect.top, depth + 1);
                  if (nested) return nested;
                }
              } catch {}
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

          const found = findCheckable(deepElementFromPoint(document, x, y));
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
          const deepElementFromPoint = (doc, pointX, pointY, depth = 0) => {
            if (!doc || depth > 12 || pointX < 0 || pointY < 0) return null;
            let element = doc.elementFromPoint(pointX, pointY);
            if (!element) return null;
            let shadowDepth = 0;
            while (element && element.shadowRoot && shadowDepth < 8) {
              const inner = element.shadowRoot.elementFromPoint(pointX, pointY);
              if (!inner || inner === element) break;
              element = inner;
              shadowDepth += 1;
            }
            if (element && element.tagName && element.tagName.toLowerCase() === 'iframe') {
              try {
                const frameDoc = element.contentDocument;
                if (frameDoc) {
                  const rect = element.getBoundingClientRect();
                  const nested = deepElementFromPoint(frameDoc, pointX - rect.left, pointY - rect.top, depth + 1);
                  if (nested) return nested;
                }
              } catch {}
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

          let node = deepElementFromPoint(document, x, y);
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
          const selector = ['a[href]','button','input','textarea','select','summary','details','[role="button"]','[role="link"]','[role="menuitem"]','[role="tab"]','[role="checkbox"]','[role="radio"]','[role="option"]','[role="switch"]','[role="combobox"]','[role="textbox"]','[role="searchbox"]','[role="slider"]','[role="spinbutton"]','[tabindex]','[contenteditable="true"]','[onclick]','[aria-haspopup]','[aria-expanded]','[aria-controls]','[aria-pressed]','[aria-selected]','[aria-checked]','[data-action]','label'].join(',');
          const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();
          const maxIframeDepth = 5;
          const maxIframeCount = 30;
          let sameOriginIframeCount = 0;
          let crossOriginIframeCount = 0;
          let visitedIframes = 0;

          const safeMatches = (el, rule) => { try { return !!el.matches && el.matches(rule); } catch { return false; } };
          const normalizeClassName = (el) => !el || !el.className ? '' : (typeof el.className === 'string' ? el.className : String(el.className.baseVal || ''));
          const cssPath = (el) => {
            if (!el || !el.tagName) return '';
            if (el.id) return `#${CSS.escape(el.id)}`;
            const parts = [];
            let node = el;
            while (node && node.nodeType === Node.ELEMENT_NODE && parts.length < 6) {
              let part = node.tagName.toLowerCase();
              const parent = node.parentElement;
              if (parent) {
                const siblings = Array.from(parent.children).filter(child => child.tagName === node.tagName);
                if (siblings.length > 1) part += `:nth-of-type(${siblings.indexOf(node) + 1})`;
              }
              parts.unshift(part);
              if (parent) node = parent;
              else {
                const root = node.getRootNode && node.getRootNode();
                if (root && root.host) {
                  parts.unshift(':shadow-root');
                  node = root.host;
                } else node = null;
              }
            }
            return parts.join(' > ');
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

          const hasEventHandler = (el) => {
            if (!el || !el.getAttributeNames) return false;
            return el.getAttributeNames().some(name => /^on(mouse|click|pointer|key|touch)/i.test(name));
          };

          const hasInteractiveAriaState = (el) => {
            if (!el || !el.hasAttribute) return false;
            return el.hasAttribute('aria-haspopup') ||
              el.hasAttribute('aria-expanded') ||
              el.hasAttribute('aria-controls') ||
              el.hasAttribute('aria-pressed') ||
              el.hasAttribute('aria-selected') ||
              el.hasAttribute('aria-checked');
          };

          const hasInteractiveRole = (el) => {
            const roleValue = (el?.getAttribute?.('role') || '').toLowerCase();
            return [
              'button', 'link', 'menuitem', 'tab', 'checkbox', 'radio', 'option', 'switch',
              'combobox', 'textbox', 'searchbox', 'slider', 'spinbutton', 'gridcell', 'row'
            ].includes(roleValue);
          };

          const hasInteractiveClassHint = (el) => {
            const className = normalizeClassName(el).toLowerCase();
            const id = (el.id || '').toLowerCase();
            const hints = ['btn', 'button', 'link', 'tab', 'toggle', 'switch', 'menu', 'dropdown', 'select', 'item', 'icon'];
            return hints.some(hint => className.includes(hint) || id.includes(hint));
          };

          const hasUsableTabIndex = (el) => {
            const raw = el?.getAttribute?.('tabindex');
            if (raw === null || raw === undefined) return false;
            const value = Number(raw);
            return Number.isFinite(value) && value >= 0;
          };

          const isLikelyInteractive = (el) => {
            if (!el) return false;
            if (safeMatches(el, selector)) return true;

            const style = window.getComputedStyle(el);
            const pointerLike = style && style.cursor === 'pointer' && style.pointerEvents !== 'none';

            return hasEventHandler(el) ||
              hasInteractiveRole(el) ||
              hasInteractiveAriaState(el) ||
              hasUsableTabIndex(el) ||
              (pointerLike && (hasInteractiveClassHint(el) || !!el.getAttribute('data-action') || !!el.getAttribute('data-testid')));
          };

          const getInputType = (el) => {
            if (!el) return '';
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
            if (!el) return false;
            const inputType = getInputType(el);
            return (el.tagName || '').toLowerCase() === 'input' &&
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

          const stableKey = (el, selectorPath, contextPath, stateElement = el) => {
            const parts = [
              contextPath,
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

          const deepElementFromPoint = (doc, x, y, depth = 0) => {
            if (!doc || depth > 12 || x < 0 || y < 0) return null;
            let element = doc.elementFromPoint(x, y);
            if (!element) return null;
            let shadowDepth = 0;
            while (element && element.shadowRoot && shadowDepth < 8) {
              const inner = element.shadowRoot.elementFromPoint(x, y);
              if (!inner || inner === element) break;
              element = inner;
              shadowDepth += 1;
            }
            if (element && element.tagName && element.tagName.toLowerCase() === 'iframe') {
              try {
                const frameDoc = element.contentDocument;
                if (frameDoc) {
                  const rect = element.getBoundingClientRect();
                  const nested = deepElementFromPoint(frameDoc, x - rect.left, y - rect.top, depth + 1);
                  if (nested) return nested;
                }
              } catch {}
            }
            return element;
          };

          const pointHitsElement = (el, x, y) => {
            if (x < 0 || y < 0 || x > window.innerWidth || y > window.innerHeight) return false;
            const hit = deepElementFromPoint(document, x, y);
            if (!hit) return false;
            return hit === el || el.contains(hit) || hit.contains(el);
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

          const isOccluderLike = (el, rect) => {
            if (!el) return false;
            const style = window.getComputedStyle(el);
            const opacity = Number(style?.opacity || '1');
            const bg = (style?.backgroundColor || '').toLowerCase();
            const cls = `${el.id || ''} ${normalizeClassName(el)}`.toLowerCase();
            const role = (el.getAttribute('role') || '').toLowerCase();
            const overlayHint = /modal|dialog|overlay|backdrop|popup|drawer/.test(cls) || role === 'dialog' || role === 'alertdialog' || el.getAttribute('aria-modal') === 'true';
            const hasBg = bg && bg !== 'rgba(0, 0, 0, 0)' && bg !== 'transparent';
            return rect.width > 24 && rect.height > 24 && opacity >= 0.7 && hasBg && (overlayHint || style?.position === 'fixed');
          };

          const intersectionArea = (a, b) => {
            const x1 = Math.max(a.left, b.left);
            const y1 = Math.max(a.top, b.top);
            const x2 = Math.min(a.right, b.right);
            const y2 = Math.min(a.bottom, b.bottom);
            return Math.max(0, x2 - x1) * Math.max(0, y2 - y1);
          };

          const collectElements = (doc, contextPath, xOffset, yOffset, depth, output = []) => {
            if (!doc || !doc.querySelectorAll) return output;
            for (const el of Array.from(doc.querySelectorAll('*'))) {
              output.push({ el, contextPath, xOffset, yOffset });
              if (el.shadowRoot) collectElements(el.shadowRoot, `${contextPath}/shadow:${(el.tagName || '').toLowerCase()}`, xOffset, yOffset, depth, output);
              if ((el.tagName || '').toLowerCase() === 'iframe' && depth < maxIframeDepth && visitedIframes < maxIframeCount) {
                visitedIframes += 1;
                const rect = el.getBoundingClientRect();
                try {
                  const frameDoc = el.contentDocument;
                  if (frameDoc) {
                    sameOriginIframeCount += 1;
                    collectElements(frameDoc, `${contextPath}/iframe:${cssPath(el) || 'frame'}`, xOffset + rect.left, yOffset + rect.top, depth + 1, output);
                  }
                } catch {
                  crossOriginIframeCount += 1;
                }
              }
            }
            return output;
          };

          const collected = collectElements(document, 'main', 0, 0, 0, []);
          const prelim = [];
          const occluders = [];
          const seen = new Set();
          for (const entry of collected) {
            const el = entry.el;
            if (seen.has(el)) continue;
            seen.add(el);
            if (!isLikelyInteractive(el)) continue;

            const rect = el.getBoundingClientRect();
            if (!isVisible(el, rect)) continue;

            const tagName = (el.tagName || '').toLowerCase();
            const stateControl = associatedControl(el);
            if (tagName === 'label' && !stateControl) continue;

            const hitInfo = topMostInfo(el, rect);
            const isTopMost = hitInfo.center || hitInfo.score >= Math.min(2, samplePoints(rect).length);
            if (!isTopMost) continue;
            const priority = hitInfo.score;
            const selectorPath = cssPath(el);
            const stateElement = stateControl || el;
            const inputType = getInputType(stateElement);
            const isEditable = isEditableElement(stateElement);
            const sensitive = isSensitiveElement(stateElement);
            const value = elementValue(stateElement);
            const isCheckable = inputType === 'checkbox' || inputType === 'radio';
            const globalRect = {
              left: rect.left + entry.xOffset,
              top: rect.top + entry.yOffset,
              right: rect.right + entry.xOffset,
              bottom: rect.bottom + entry.yOffset
            };
            const candidate = {
              tagName,
              role: stateElement.getAttribute('role') || el.getAttribute('role') || '',
              text: elementText(el),
              ariaLabel: normalize(stateElement.getAttribute('aria-label') || el.getAttribute('aria-label') || ''),
              placeholder: normalize(stateElement.getAttribute('placeholder') || el.getAttribute('placeholder') || ''),
              href: el.href || el.getAttribute('href') || '',
              selector: selectorPath,
              stableKey: stableKey(el, selectorPath, entry.contextPath, stateElement),
              value,
              inputType,
              x: rect.x + entry.xOffset,
              y: rect.y + entry.yOffset,
              width: rect.width,
              height: rect.height,
              isVisible: true,
              isEnabled: isEnabled(stateElement),
              isEditable,
              isChecked: isCheckable ? !!stateElement.checked : null,
              isSensitive: sensitive,
              isTopMost: true,
              priority,
              visibilityScore: hitInfo.score,
              contextPath: entry.contextPath
            };
            prelim.push({ candidate, globalRect, priority });
            if (isOccluderLike(el, rect)) occluders.push({ rect: globalRect, priority });
          }

          const sortedOccluders = occluders.sort((a, b) => b.priority - a.priority);
          const kept = [];
          for (const item of prelim.sort((a, b) => b.priority - a.priority)) {
            const totalArea = Math.max(1, item.globalRect.right - item.globalRect.left) * Math.max(1, item.globalRect.bottom - item.globalRect.top);
            let covered = 0;
            for (const occ of sortedOccluders) {
              covered += intersectionArea(item.globalRect, occ.rect);
              if ((covered / totalArea) >= 0.90) break;
            }
            if ((covered / totalArea) < 0.90) kept.push(item.candidate);
          }

          return {
            elements: kept,
            prePaintCandidateCount: prelim.length,
            postPaintCandidateCount: kept.length,
            sameOriginIframeCount,
            crossOriginIframeCount
          };
        }
        """;

    private sealed record RuntimeSession(IBrowserContext Context, IPage Page, BrowserSessionOptions Options, string? StorageStatePath)
    {
        public ConcurrentDictionary<string, SomElement> Elements { get; } = new();
        public string? LastObservationFallbackReason { get; set; }
        public int NextElementOrdinal { get; set; }
        public List<SomElement> LastElements { get; } = new();
        public HashSet<string> SeenElementIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> StableKeyToElementId { get; } = new(StringComparer.OrdinalIgnoreCase);
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
