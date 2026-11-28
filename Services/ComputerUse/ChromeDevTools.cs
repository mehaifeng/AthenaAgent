using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.ComputerUse;

/// <summary>Private in-process CEF DevTools Protocol channel for a single tab (no TCP debugging port).</summary>
internal sealed class ChromeDevTools
{
    private readonly CefNativeSession? _session;
    private readonly int _tabId;
    private readonly Func<string, object, CancellationToken, Task<JsonElement>>? _customCaller;

    public ChromeDevTools(CefNativeSession session, int tabId)
    {
        _session = session;
        _tabId = tabId;
    }

    internal ChromeDevTools(Func<string, object, CancellationToken, Task<JsonElement>> customCaller)
    {
        _customCaller = customCaller;
    }

    public bool IsOpen => _customCaller is not null || _session?.IsOpen == true;

    public Task<JsonElement> CallAsync(string method, object parameters, CancellationToken token)
    {
        if (_customCaller is not null)
            return _customCaller(method, parameters, token);
        if (_session is null || !_session.IsOpen)
            throw new ChromeDevToolsException("The browser runtime disconnected.");
        return _session.SendDevToolsAsync(_tabId, method, parameters, token);
    }
}

internal sealed class ChromeDevToolsException(string message) : InvalidOperationException(message)
{
    public bool IsNavigation =>
        Message.Contains("Cannot find context", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("context was destroyed", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("Inspected target navigated", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("No frame with given id", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("Not attached to an active page", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("target closed while handling command", StringComparison.OrdinalIgnoreCase);
}
