using Athena.UI.Services.ComputerUse;
using Athena.UI.Services.Interfaces;
using Athena.UI.Services.SubAgents;
using System;
using System.Threading.Tasks;

namespace Athena.UI.Services.Functions;

public sealed class ComputerUseFunctions(ComputerUseService service)
{
    public bool IsAvailable => service.IsAvailable;

    public async Task<FunctionResult> RunComputerTaskAsync(string instruction, string? startingUrl = null, string? target = null)
    {
        if (ToolApprovalContext.CurrentMode != ToolApprovalContext.ExecutionMode.Interactive)
            return FunctionResult.FailureResult("Computer Use requires an interactive conversation.");
        if (string.IsNullOrWhiteSpace(instruction))
            return FunctionResult.FailureResult("A Computer Use instruction is required.");
        if (target is not (null or "desktop" or "browser") || target == "desktop" && startingUrl is not null)
            return FunctionResult.FailureResult("Choose desktop, or choose browser with an optional startingUrl.");
        var destination = ResolveTarget(startingUrl, target, service.SelectedTarget, service.CurrentBrowserTarget);
        if (destination is null)
            return FunctionResult.FailureResult(startingUrl is null
                ? "Open a webpage in the built-in browser or provide a startingUrl."
                : "startingUrl must be a complete HTTP or HTTPS address without embedded credentials.");
        try
        {
            return await service.RunAsync(destination, instruction, ToolExecutionContext.CurrentCancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            return FunctionResult.FailureResult(exception.Message);
        }
    }

    internal static ComputerUseTarget? ResolveTarget(string? startingUrl, string? target,
        ComputerUseTarget? selected, ComputerUseTarget? currentBrowser) => target switch
    {
        "desktop" => ComputerUseTarget.Desktop,
        _ when startingUrl is not null => ComputerUseTarget.FromBrowserAddress(startingUrl),
        "browser" => currentBrowser,
        _ => selected ?? ComputerUseTarget.Desktop
    };
}
