using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Browser;

public abstract class BrowserEvent
{
    public string EventId { get; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; init; } = string.Empty;
    public DateTime CreatedAt { get; } = DateTime.Now;
    public bool IsHandled { get; private set; }

    public void MarkHandled() => IsHandled = true;
}

public abstract class BrowserEvent<TResult> : BrowserEvent
{
    public TResult? Result { get; private set; }

    public void Complete(TResult result)
    {
        Result = result;
        MarkHandled();
    }
}

public sealed class BrowserEventBus
{
    private readonly ConcurrentDictionary<Type, List<Func<BrowserEvent, CancellationToken, Task>>> _handlers = new();
    private readonly ILogger _logger;

    public BrowserEventBus(ILogger logger)
    {
        _logger = logger.ForContext<BrowserEventBus>();
    }

    public void Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler)
        where TEvent : BrowserEvent
    {
        var handlers = _handlers.GetOrAdd(typeof(TEvent), _ => new List<Func<BrowserEvent, CancellationToken, Task>>());
        lock (handlers)
        {
            handlers.Add((browserEvent, cancellationToken) => handler((TEvent)browserEvent, cancellationToken));
        }
    }

    public async Task DispatchAsync<TEvent>(TEvent browserEvent, CancellationToken cancellationToken = default)
        where TEvent : BrowserEvent
    {
        if (!_handlers.TryGetValue(typeof(TEvent), out var handlers))
        {
            return;
        }

        List<Func<BrowserEvent, CancellationToken, Task>> snapshot;
        lock (handlers)
        {
            snapshot = handlers.ToList();
        }

        foreach (var handler in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (browserEvent.IsHandled)
            {
                break;
            }

            try
            {
                await handler(browserEvent, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning(ex, "Browser event handler failed. EventType={EventType}, EventId={EventId}", typeof(TEvent).Name, browserEvent.EventId);
                throw;
            }
        }
    }

    public async Task<TResult> DispatchForResultAsync<TEvent, TResult>(TEvent browserEvent, CancellationToken cancellationToken = default)
        where TEvent : BrowserEvent<TResult>
    {
        await DispatchAsync(browserEvent, cancellationToken);
        if (!browserEvent.IsHandled)
        {
            throw new InvalidOperationException($"Browser event was not handled: {typeof(TEvent).Name}");
        }

        return browserEvent.Result!;
    }
}

public sealed class BrowserSessionStartedEvent : BrowserEvent<bool>
{
}

public sealed class BrowserSessionStoppingEvent : BrowserEvent<bool>
{
}

public sealed class BrowserStateRequestEvent : BrowserEvent<BrowserStateSummary>
{
    public bool IncludeScreenshot { get; init; } = true;
}

public sealed class BrowserSecurityCheckEvent : BrowserEvent<BrowserSecurityDecision>
{
    public BrowserAgentAction Action { get; init; } = new();
    public BrowserActionDefinition Definition { get; init; } = new();
}

public sealed class BrowserAgentActionEvent : BrowserEvent<BrowserActionResult>
{
    public BrowserAgentAction Action { get; init; } = new();
    public BrowserActionDefinition Definition { get; init; } = new();
}

public sealed class BrowserSecurityDecision
{
    public bool Allowed { get; init; } = true;
    public BrowserActionResult? BlockResult { get; init; }

    public static BrowserSecurityDecision Allow() => new() { Allowed = true };

    public static BrowserSecurityDecision Block(BrowserActionResult result) => new()
    {
        Allowed = false,
        BlockResult = result
    };
}

public sealed class BrowserSession : IAsyncDisposable
{
    private readonly IHeadlessBrowserService _browserService;
    private readonly ILogger _logger;
    private readonly List<BrowserWatchdog> _watchdogs = new();
    private bool _started;
    private bool _stopped;

    private BrowserSession(IHeadlessBrowserService browserService, BrowserSessionInfo info, BrowserSessionOptions options, ILogger logger)
    {
        _browserService = browserService;
        Info = info;
        Options = options;
        _logger = logger.ForContext<BrowserSession>();
        EventBus = new BrowserEventBus(_logger);
        AttachWatchdogs();
    }

    public BrowserSessionInfo Info { get; }
    public BrowserSessionOptions Options { get; }
    public BrowserEventBus EventBus { get; }
    public BrowserStateSummary? CachedState { get; private set; }
    public int DomVersion { get; private set; }
    public string SessionId => Info.SessionId;
    public string? CurrentUrl { get; private set; }

    public static async Task<BrowserSession> CreateAsync(
        IHeadlessBrowserService browserService,
        BrowserSessionOptions options,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var info = await browserService.CreateSessionAsync(options, cancellationToken);
        return new BrowserSession(browserService, info, options, logger);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        var started = new BrowserSessionStartedEvent { SessionId = SessionId };
        started.Complete(true);
        await EventBus.DispatchAsync(started, cancellationToken);
        _logger.Information("Browser session started. SessionId={SessionId}", SessionId);
    }

    public async Task<BrowserStateSummary> GetStateAsync(bool includeScreenshot, CancellationToken cancellationToken = default)
    {
        var state = await EventBus.DispatchForResultAsync<BrowserStateRequestEvent, BrowserStateSummary>(
            new BrowserStateRequestEvent
            {
                SessionId = SessionId,
                IncludeScreenshot = includeScreenshot
            },
            cancellationToken);

        CachedState = state;
        DomVersion = state.DomVersion;
        CurrentUrl = state.Url;
        return state;
    }

    public async Task<BrowserActionResult> ExecuteActionAsync(
        BrowserAgentAction action,
        BrowserActionDefinition definition,
        CancellationToken cancellationToken = default)
    {
        var security = await EventBus.DispatchForResultAsync<BrowserSecurityCheckEvent, BrowserSecurityDecision>(
            new BrowserSecurityCheckEvent
            {
                SessionId = SessionId,
                Action = action,
                Definition = definition
            },
            cancellationToken);

        if (!security.Allowed)
        {
            return security.BlockResult ?? ActionFailure(definition.Type, action.Name, "Browser action blocked by security policy.");
        }

        var result = await EventBus.DispatchForResultAsync<BrowserAgentActionEvent, BrowserActionResult>(
            new BrowserAgentActionEvent
            {
                SessionId = SessionId,
                Action = action,
                Definition = definition
            },
            cancellationToken);

        result.SessionId ??= SessionId;
        result.Action = result.Action == BrowserActionType.None ? definition.Type : result.Action;
        result.ActionName ??= action.Name;
        if (!string.IsNullOrWhiteSpace(result.Url))
        {
            CurrentUrl = result.Url;
        }

        return result;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        await EventBus.DispatchAsync(new BrowserSessionStoppingEvent { SessionId = SessionId }, cancellationToken);
        await _browserService.CloseSessionAsync(SessionId, cancellationToken);
        _logger.Information("Browser session stopped. SessionId={SessionId}", SessionId);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private void AttachWatchdogs()
    {
        _watchdogs.Add(new BrowserSecurityWatchdog(this, _logger));
        _watchdogs.Add(new BrowserStateWatchdog(this, _browserService, _logger));
        _watchdogs.Add(new BrowserNavigationWatchdog(this, _browserService, _logger));
        _watchdogs.Add(new BrowserDefaultActionWatchdog(this, _browserService, _logger));

        foreach (var watchdog in _watchdogs)
        {
            watchdog.Attach();
        }
    }

    private BrowserActionResult ActionFailure(BrowserActionType type, string actionName, string message) => new()
    {
        Success = false,
        Action = type,
        ActionName = actionName,
        SessionId = SessionId,
        Message = message,
        Error = message
    };
}

public abstract class BrowserWatchdog
{
    protected BrowserWatchdog(BrowserSession session, ILogger logger)
    {
        Session = session;
        Logger = logger.ForContext(GetType());
    }

    protected BrowserSession Session { get; }
    protected ILogger Logger { get; }

    public abstract void Attach();

    protected BrowserActionResult Success(BrowserActionType type, string actionName, string message, string? url = null) => new()
    {
        Success = true,
        Action = type,
        ActionName = actionName,
        SessionId = Session.SessionId,
        Message = message,
        Url = url ?? Session.CurrentUrl,
        ExtractedContent = message,
        LongTermMemory = message
    };

    protected BrowserActionResult Failure(BrowserActionType type, string actionName, string message, BrowserRiskType risk = BrowserRiskType.None) => new()
    {
        Success = false,
        Action = type,
        ActionName = actionName,
        SessionId = Session.SessionId,
        Message = message,
        Error = message,
        Risk = risk
    };
}

public sealed class BrowserSecurityWatchdog : BrowserWatchdog
{
    private static readonly string[] DangerousEvaluateTokens =
    [
        "document.cookie",
        "localStorage",
        "sessionStorage",
        "indexedDB",
        "fetch(",
        "XMLHttpRequest",
        "sendBeacon",
        "window.location",
        "location.href",
        "eval(",
        "Function("
    ];

    public BrowserSecurityWatchdog(BrowserSession session, ILogger logger)
        : base(session, logger)
    {
    }

    public override void Attach()
    {
        Session.EventBus.Subscribe<BrowserSecurityCheckEvent>(OnSecurityCheckAsync);
    }

    private Task OnSecurityCheckAsync(BrowserSecurityCheckEvent browserEvent, CancellationToken cancellationToken)
    {
        var action = browserEvent.Action;
        var name = BrowserActionRegistry.NormalizeName(action.Name);

        if (name is "navigate")
        {
            var url = action.GetString("url");
            var block = ValidateUrl(url, browserEvent.Definition.Type, name);
            if (block != null)
            {
                browserEvent.Complete(BrowserSecurityDecision.Block(block));
                return Task.CompletedTask;
            }
        }

        if (name is "search")
        {
            var query = action.GetString("query");
            if (string.IsNullOrWhiteSpace(query))
            {
                browserEvent.Complete(BrowserSecurityDecision.Block(Failure(browserEvent.Definition.Type, name, "Search action requires a query.")));
                return Task.CompletedTask;
            }
        }

        if (name is "upload_file" && string.IsNullOrWhiteSpace(action.GetString("path")))
        {
            browserEvent.Complete(BrowserSecurityDecision.Block(Failure(BrowserActionType.Upload, name, "Upload action requires a file path.", BrowserRiskType.Upload)));
            return Task.CompletedTask;
        }

        if ((action.GetBool("expect_download") ?? action.GetBool("expectDownload") ?? false) && !Session.Options.DownloadEnabled)
        {
            browserEvent.Complete(BrowserSecurityDecision.Block(Failure(
                browserEvent.Definition.Type,
                name,
                "Download was requested but browser downloads are disabled in settings.",
                BrowserRiskType.Download)));
            return Task.CompletedTask;
        }

        if (name is "evaluate")
        {
            var code = action.GetString("code") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(code))
            {
                browserEvent.Complete(BrowserSecurityDecision.Block(Failure(BrowserActionType.Evaluate, name, "Evaluate action requires code.")));
                return Task.CompletedTask;
            }

            if (code.Length > 4000 || DangerousEvaluateTokens.Any(token => code.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                browserEvent.Complete(BrowserSecurityDecision.Block(Failure(
                    BrowserActionType.Evaluate,
                    name,
                    "Evaluate action blocked by browser security policy.",
                    BrowserRiskType.DestructiveAction)));
                return Task.CompletedTask;
            }
        }

        browserEvent.Complete(BrowserSecurityDecision.Allow());
        return Task.CompletedTask;
    }

    private BrowserActionResult? ValidateUrl(string? rawUrl, BrowserActionType type, string actionName)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)
            || !Uri.TryCreate(rawUrl, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            return Failure(type, actionName, "Navigation requires an absolute http or https URL.", BrowserRiskType.Navigation);
        }

        if (IsLocalOrPrivateHost(url.Host))
        {
            return Failure(type, actionName, $"Navigation to local or private network hosts is blocked: {url.Host}", BrowserRiskType.LocalNetwork);
        }

        return null;
    }

    private static bool IsLocalOrPrivateHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(host, out var address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return IPAddress.IsLoopback(address)
            || bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254);
    }
}

public sealed class BrowserStateWatchdog : BrowserWatchdog
{
    private readonly IHeadlessBrowserService _browserService;

    public BrowserStateWatchdog(BrowserSession session, IHeadlessBrowserService browserService, ILogger logger)
        : base(session, logger)
    {
        _browserService = browserService;
    }

    public override void Attach()
    {
        Session.EventBus.Subscribe<BrowserStateRequestEvent>(OnStateRequestAsync);
    }

    private async Task OnStateRequestAsync(BrowserStateRequestEvent browserEvent, CancellationToken cancellationToken)
    {
        try
        {
            var observation = await _browserService.ObserveAsync(Session.SessionId, cancellationToken);
            browserEvent.Complete(CreateStateFromObservation(observation, browserEvent.IncludeScreenshot));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "Browser state request failed. SessionId={SessionId}", Session.SessionId);

            if (_browserService is PlaywrightBrowserService playwrightService && LooksLikeInteractiveExtractionException(ex))
            {
                try
                {
                    var fallbackObservation = await playwrightService.ObserveWithoutElementsAsync(Session.SessionId, ex.Message, cancellationToken);
                    var fallback = CreateStateFromObservation(fallbackObservation, includeScreenshot: false);
                    fallback.BrowserErrors.Add("Interactive element extraction failed. Fallback to DomOnly is applied for the next step.");
                    fallback.BrowserErrors.Add(ex.Message);
                    fallback.Metadata["fallbackMode"] = "DomOnlyNextStep";
                    browserEvent.Complete(fallback);
                    return;
                }
                catch (Exception fallbackEx) when (fallbackEx is not OperationCanceledException)
                {
                    Logger.Warning(fallbackEx, "Browser fallback state capture failed. SessionId={SessionId}", Session.SessionId);
                }
            }

            browserEvent.Complete(new BrowserStateSummary
            {
                SessionId = Session.SessionId,
                Url = Session.CurrentUrl,
                DomVersion = Session.DomVersion,
                BrowserErrors = { ex.Message }
            });
        }
    }

    private BrowserStateSummary CreateStateFromObservation(SomObservation observation, bool includeScreenshot)
    {
        var domVersion = Session.DomVersion + 1;
        var tabs = _browserService is PlaywrightBrowserService playwrightService
            ? playwrightService.GetTabsSnapshot(Session.SessionId)
            : new List<BrowserTabInfo>
            {
                new()
                {
                    TabId = "main",
                    Url = observation.Url,
                    Title = observation.Title,
                    IsActive = true
                }
            };

        if (tabs.Count == 0)
        {
            tabs.Add(new BrowserTabInfo
            {
                TabId = "main",
                Url = observation.Url,
                Title = observation.Title,
                IsActive = true
            });
        }

        return new BrowserStateSummary
        {
            SessionId = Session.SessionId,
            Url = observation.Url,
            Title = observation.Title,
            Observation = observation,
            ScreenshotBase64 = includeScreenshot ? observation.ScreenshotBase64 : null,
            ScreenshotMimeType = includeScreenshot ? observation.ScreenshotMimeType : null,
            Elements = observation.Elements,
            DomVersion = domVersion,
            CapturedAt = observation.CapturedAt,
            Tabs = tabs,
            PageInfo = new BrowserPageInfo
            {
                ViewportWidth = observation.ViewportWidth,
                ViewportHeight = observation.ViewportHeight,
                ScrollX = observation.ScrollX,
                ScrollY = observation.ScrollY,
                PixelsAbove = Math.Max(0, observation.ScrollY)
            }
        };
    }

    private static bool LooksLikeInteractiveExtractionException(Exception ex)
    {
        var message = ex.Message ?? string.Empty;
        return message.Contains("tagName", StringComparison.OrdinalIgnoreCase)
            || message.Contains("InteractiveElementsScript", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Cannot read properties of null", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class BrowserNavigationWatchdog : BrowserWatchdog
{
    private readonly IHeadlessBrowserService _browserService;

    public BrowserNavigationWatchdog(BrowserSession session, IHeadlessBrowserService browserService, ILogger logger)
        : base(session, logger)
    {
        _browserService = browserService;
    }

    public override void Attach()
    {
        Session.EventBus.Subscribe<BrowserAgentActionEvent>(OnActionAsync);
    }

    private async Task OnActionAsync(BrowserAgentActionEvent browserEvent, CancellationToken cancellationToken)
    {
        var name = BrowserActionRegistry.NormalizeName(browserEvent.Action.Name);
        if (name is not ("navigate" or "search" or "go_back"))
        {
            return;
        }

        BrowserActionResult result = name switch
        {
            "navigate" => await _browserService.NavigateAsync(Session.SessionId, browserEvent.Action.GetString("url") ?? string.Empty, cancellationToken),
            "search" => await _browserService.NavigateAsync(Session.SessionId, BuildSearchUrl(browserEvent.Action), cancellationToken),
            "go_back" => await _browserService.PressKeyAsync(Session.SessionId, "Alt+ArrowLeft", cancellationToken),
            _ => Failure(browserEvent.Definition.Type, name, $"Unsupported navigation action: {name}")
        };

        result.Action = browserEvent.Definition.Type;
        result.ActionName = name;
        result.ExtractedContent ??= result.Message;
        result.LongTermMemory ??= result.Message;
        browserEvent.Complete(result);
    }

    private static string BuildSearchUrl(BrowserAgentAction action)
    {
        var query = Uri.EscapeDataString(action.GetString("query") ?? string.Empty);
        var engine = (action.GetString("engine") ?? "duckduckgo").Trim().ToLowerInvariant();
        return engine switch
        {
            "google" => $"https://www.google.com/search?q={query}&udm=14",
            "bing" => $"https://www.bing.com/search?q={query}",
            _ => $"https://duckduckgo.com/?q={query}"
        };
    }
}

public sealed class BrowserDefaultActionWatchdog : BrowserWatchdog
{
    private readonly IHeadlessBrowserService _browserService;

    public BrowserDefaultActionWatchdog(BrowserSession session, IHeadlessBrowserService browserService, ILogger logger)
        : base(session, logger)
    {
        _browserService = browserService;
    }

    public override void Attach()
    {
        Session.EventBus.Subscribe<BrowserAgentActionEvent>(OnActionAsync);
    }

    private async Task OnActionAsync(BrowserAgentActionEvent browserEvent, CancellationToken cancellationToken)
    {
        var action = browserEvent.Action;
        var definition = browserEvent.Definition;
        var name = BrowserActionRegistry.NormalizeName(action.Name);

        BrowserActionResult result = name switch
        {
            "wait" => await _browserService.WaitAsync(Session.SessionId, ResolveWaitMilliseconds(action), cancellationToken),
            "click" => await _browserService.ClickAsync(Session.SessionId, ResolveElementId(action), cancellationToken),
            "input" => await _browserService.TypeAsync(Session.SessionId, ResolveElementId(action), action.GetString("text") ?? string.Empty, cancellationToken),
            "upload_file" => await _browserService.UploadAsync(Session.SessionId, ResolveElementId(action), action.GetString("path") ?? string.Empty, cancellationToken),
            "scroll" => await _browserService.ScrollAsync(Session.SessionId, action.GetInt32("deltaX") ?? 0, ResolveScrollDelta(action), cancellationToken),
            "send_keys" => await _browserService.PressKeyAsync(Session.SessionId, action.GetString("keys") ?? action.GetString("key") ?? string.Empty, cancellationToken),
            "extract" => await ExtractAsync(action, cancellationToken),
            "search_page" => await SearchPageAsync(action, cancellationToken),
            "find_elements" => FindElements(action),
            "find_text" => await FindTextAsync(action, cancellationToken),
            "screenshot" => await ScreenshotAsync(cancellationToken),
            "save_as_pdf" => await _browserService.SavePdfAsync(Session.SessionId, action.GetString("file_name") ?? action.GetString("fileName"), cancellationToken),
            "dropdown_options" => DropdownOptions(action),
            "select_dropdown" => await _browserService.SelectAsync(Session.SessionId, ResolveElementId(action), action.GetString("text") ?? string.Empty, cancellationToken),
            "switch_tab" => await SwitchTabAsync(action, cancellationToken),
            "close_tab" => await CloseTabAsync(action, cancellationToken),
            "evaluate" => await _browserService.EvaluateAsync(Session.SessionId, action.GetString("code") ?? string.Empty, cancellationToken),
            "done" => Done(action),
            _ => Failure(definition.Type, name, $"Unsupported browser action: {name}")
        };

        result.Action = result.Action == BrowserActionType.None ? definition.Type : result.Action;
        result.ActionName = name;
        result.ExtractedContent ??= result.ExtractedText ?? result.Message;
        result.LongTermMemory ??= result.Message;
        browserEvent.Complete(result);
    }

    private string ResolveElementId(BrowserAgentAction action)
    {
        var elementId = action.GetString("elementId")
            ?? action.GetString("targetElementId")
            ?? action.GetString("target_element_id");

        if (!string.IsNullOrWhiteSpace(elementId))
        {
            return NormalizeElementId(elementId) ?? elementId;
        }

        var index = action.GetInt32("index");
        if (index.HasValue)
        {
            var indexed = Session.CachedState?.Elements.FirstOrDefault(element => element.Index == index.Value);
            if (indexed != null)
            {
                return indexed.ElementId;
            }
        }

        return string.Empty;
    }

    private string? NormalizeElementId(string elementId)
    {
        var elements = Session.CachedState?.Elements ?? new List<SomElement>();
        var exact = elements.FirstOrDefault(element => string.Equals(element.ElementId, elementId, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact.ElementId;
        }

        if (elementId.StartsWith("som-", StringComparison.OrdinalIgnoreCase) && int.TryParse(elementId[4..], out var somIndex))
        {
            return elements.FirstOrDefault(element => element.Index == somIndex)?.ElementId;
        }

        return int.TryParse(elementId, out var index)
            ? elements.FirstOrDefault(element => element.Index == index)?.ElementId
            : null;
    }

    private static int ResolveScrollDelta(BrowserAgentAction action)
    {
        if (action.GetInt32("deltaY") is { } deltaY)
        {
            return deltaY;
        }

        var pages = action.GetDouble("pages") ?? 1.0;
        var down = action.GetBool("down") ?? true;
        var pixels = (int)Math.Clamp(pages * 700, 100, 7000);
        return down ? pixels : -pixels;
    }

    private static int ResolveWaitMilliseconds(BrowserAgentAction action)
    {
        if (action.GetInt32("milliseconds") is { } milliseconds)
        {
            return Math.Clamp(milliseconds, 0, 30000);
        }

        var seconds = action.GetDouble("seconds") ?? 1.0;
        return Math.Clamp((int)Math.Round(seconds * 1000), 0, 30000);
    }

    private async Task<BrowserActionResult> ExtractAsync(BrowserAgentAction action, CancellationToken cancellationToken)
    {
        var result = await _browserService.ExtractTextAsync(Session.SessionId, cancellationToken);
        var query = action.GetString("query");
        if (!string.IsNullOrWhiteSpace(query) && !string.IsNullOrWhiteSpace(result.ExtractedText))
        {
            result.ExtractedContent = LimitText(FindRelevantText(result.ExtractedText, query), 12000);
        }
        else
        {
            result.ExtractedContent = LimitText(result.ExtractedText, 12000);
        }

        result.LongTermMemory = string.IsNullOrWhiteSpace(query)
            ? "Extracted visible page text."
            : $"Extracted page text for query: {query}";
        return result;
    }

    private async Task<BrowserActionResult> SwitchTabAsync(BrowserAgentAction action, CancellationToken cancellationToken)
    {
        if (_browserService is not PlaywrightBrowserService playwrightService)
        {
            return Failure(BrowserActionType.SwitchTab, "switch_tab", "Switch tab is not available for the current browser runtime.");
        }

        var tabId = action.GetString("tab_id") ?? action.GetString("tabId") ?? string.Empty;
        return await playwrightService.SwitchTabAsync(Session.SessionId, tabId, cancellationToken);
    }

    private async Task<BrowserActionResult> CloseTabAsync(BrowserAgentAction action, CancellationToken cancellationToken)
    {
        if (_browserService is not PlaywrightBrowserService playwrightService)
        {
            return Failure(BrowserActionType.CloseTab, "close_tab", "Close tab is not available for the current browser runtime.");
        }

        var tabId = action.GetString("tab_id") ?? action.GetString("tabId") ?? string.Empty;
        return await playwrightService.CloseTabAsync(Session.SessionId, tabId, cancellationToken);
    }

    private async Task<BrowserActionResult> SearchPageAsync(BrowserAgentAction action, CancellationToken cancellationToken)
    {
        var result = await _browserService.ExtractTextAsync(Session.SessionId, cancellationToken);
        if (!result.Success)
        {
            return result;
        }

        var pattern = action.GetString("pattern") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return Failure(BrowserActionType.SearchPage, "search_page", "search_page requires a pattern.");
        }

        var regex = action.GetBool("regex") ?? false;
        var caseSensitive = action.GetBool("case_sensitive") ?? action.GetBool("caseSensitive") ?? false;
        var contextChars = Math.Clamp(action.GetInt32("context_chars") ?? action.GetInt32("contextChars") ?? 150, 0, 1000);
        var maxResults = Math.Clamp(action.GetInt32("max_results") ?? action.GetInt32("maxResults") ?? 25, 1, 100);
        var matches = FindTextMatches(result.ExtractedText ?? string.Empty, pattern, regex, caseSensitive, contextChars, maxResults);
        var formatted = matches.Count == 0
            ? $"No matches found for `{pattern}`."
            : string.Join(Environment.NewLine + Environment.NewLine, matches);

        return Success(BrowserActionType.SearchPage, "search_page", $"Searched page for `{pattern}`: {matches.Count} match(es).").With(result =>
        {
            result.ExtractedContent = formatted;
            result.LongTermMemory = $"Searched page for `{pattern}`.";
        });
    }

    private BrowserActionResult FindElements(BrowserAgentAction action)
    {
        var selector = action.GetString("selector") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(selector))
        {
            return Failure(BrowserActionType.FindElements, "find_elements", "find_elements requires a selector.");
        }

        var maxResults = Math.Clamp(action.GetInt32("max_results") ?? action.GetInt32("maxResults") ?? 50, 1, 200);
        var elements = Session.CachedState?.Elements ?? new List<SomElement>();
        var normalizedSelector = selector.Trim().TrimStart('.').TrimStart('#');
        var matches = elements
            .Where(element => ElementMatches(element, normalizedSelector))
            .Take(maxResults)
            .Select(element => new
            {
                index = element.Index,
                id = element.ElementId,
                tag = element.TagName,
                role = element.Role,
                text = element.Text,
                ariaLabel = element.AriaLabel,
                placeholder = element.Placeholder,
                href = element.Href
            })
            .ToList();

        return Success(BrowserActionType.FindElements, "find_elements", $"Found {matches.Count} visible indexed element(s) matching `{selector}`.").With(result =>
        {
            result.ExtractedContent = JsonSerializer.Serialize(matches);
            result.LongTermMemory = $"Queried visible indexed elements for `{selector}`.";
        });
    }

    private async Task<BrowserActionResult> FindTextAsync(BrowserAgentAction action, CancellationToken cancellationToken)
    {
        var text = action.GetString("text") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return Failure(BrowserActionType.FindText, "find_text", "find_text requires text.");
        }

        var search = await SearchPageAsync(new BrowserAgentAction
        {
            Name = "search_page",
            Parameters = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["pattern"] = JsonSerializer.SerializeToElement(text)
            }
        }, cancellationToken);

        if (search.Success && !string.IsNullOrWhiteSpace(search.ExtractedContent) && !search.ExtractedContent.StartsWith("No matches", StringComparison.OrdinalIgnoreCase))
        {
            return Success(BrowserActionType.FindText, "find_text", $"Text found: {text}").With(result =>
            {
                result.ExtractedContent = search.ExtractedContent;
            });
        }

        return Failure(BrowserActionType.FindText, "find_text", $"Text not found: {text}");
    }

    private async Task<BrowserActionResult> ScreenshotAsync(CancellationToken cancellationToken)
    {
        var state = await Session.GetStateAsync(includeScreenshot: true, cancellationToken);
        var path = state.Observation?.AnnotatedScreenshotPath;
        var result = Success(BrowserActionType.Screenshot, "screenshot", path == null ? "Screenshot captured." : $"Screenshot captured: {path}", state.Url);
        if (!string.IsNullOrWhiteSpace(path))
        {
            result.Attachments.Add(path);
            result.Metadata["annotatedScreenshotPath"] = path;
        }

        result.Observation = state.Observation;
        return result;
    }

    private BrowserActionResult DropdownOptions(BrowserAgentAction action)
    {
        var elementId = ResolveElementId(action);
        var element = Session.CachedState?.Elements.FirstOrDefault(item => item.ElementId == elementId);
        if (element == null)
        {
            return Failure(BrowserActionType.DropdownOptions, "dropdown_options", "Dropdown element was not found in the current browser state.");
        }

        return Success(BrowserActionType.DropdownOptions, "dropdown_options", $"Dropdown candidate: {element.Text ?? element.AriaLabel ?? element.Placeholder ?? element.TagName}.").With(result =>
        {
            result.ExtractedContent = JsonSerializer.Serialize(element);
            result.IncludeExtractedContentOnlyOnce = true;
        });
    }

    private BrowserActionResult Done(BrowserAgentAction action)
    {
        var text = action.GetString("text") ?? action.GetString("message") ?? "Browser task marked as done.";
        var success = action.GetBool("success") ?? true;
        return new BrowserActionResult
        {
            Success = success,
            CompletionSuccess = success,
            IsDone = true,
            Action = BrowserActionType.Finish,
            ActionName = "done",
            SessionId = Session.SessionId,
            Url = Session.CurrentUrl,
            Message = text,
            ExtractedContent = text,
            LongTermMemory = text,
            Error = success ? null : text
        };
    }

    private static bool ElementMatches(SomElement element, string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return false;
        }

        return string.Equals(element.TagName, selector, StringComparison.OrdinalIgnoreCase)
            || string.Equals(element.Role, selector, StringComparison.OrdinalIgnoreCase)
            || Contains(element.Text, selector)
            || Contains(element.AriaLabel, selector)
            || Contains(element.Placeholder, selector)
            || Contains(element.Href, selector);
    }

    private static bool Contains(string? value, string pattern) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains(pattern, StringComparison.OrdinalIgnoreCase);

    private static string FindRelevantText(string text, string query)
    {
        var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return text;
        }

        var start = Math.Max(0, index - 2000);
        var length = Math.Min(text.Length - start, query.Length + 4000);
        return text.Substring(start, length);
    }

    private static List<string> FindTextMatches(string text, string pattern, bool regex, bool caseSensitive, int contextChars, int maxResults)
    {
        var matches = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return matches;
        }

        if (regex)
        {
            var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            foreach (Match match in Regex.Matches(text, pattern, options).Take(maxResults))
            {
                matches.Add(FormatMatch(text, match.Index, match.Length, contextChars));
            }

            return matches;
        }

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var startIndex = 0;
        while (matches.Count < maxResults)
        {
            var index = text.IndexOf(pattern, startIndex, comparison);
            if (index < 0)
            {
                break;
            }

            matches.Add(FormatMatch(text, index, pattern.Length, contextChars));
            startIndex = index + Math.Max(1, pattern.Length);
        }

        return matches;
    }

    private static string FormatMatch(string text, int index, int length, int contextChars)
    {
        var start = Math.Max(0, index - contextChars);
        var end = Math.Min(text.Length, index + length + contextChars);
        return text[start..end].ReplaceLineEndings(" ").Trim();
    }

    private static string? LimitText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return value.Length <= maxLength ? value : value[..maxLength] + "\n... [truncated]";
    }
}

public static class BrowserActionResultExtensions
{
    public static BrowserActionResult With(this BrowserActionResult result, Action<BrowserActionResult> apply)
    {
        apply(result);
        return result;
    }
}
