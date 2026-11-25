using Athena.UI.Services.ComputerUse;
using Athena.UI.Services.Interfaces;
using Athena.UI.Services.SubAgents;
using System;
using System.Threading.Tasks;

namespace Athena.UI.Services.Functions;

public sealed class ComputerUseFunctions(ComputerUseService service)
{
    public bool IsAvailable => service.IsAvailable;

    public async Task<FunctionResult> RunComputerTaskAsync(string instruction, string? startingUrl = null)
    {
        if (ToolApprovalContext.CurrentMode != ToolApprovalContext.ExecutionMode.Interactive)
            return FunctionResult.FailureResult("Computer Use requires an interactive conversation.");
        if (string.IsNullOrWhiteSpace(instruction))
            return FunctionResult.FailureResult("A Computer Use instruction is required.");
        var target = startingUrl is null
            ? service.SelectedTarget ?? service.CurrentBrowserTarget
            : ComputerUseTarget.FromBrowserAddress(startingUrl);
        if (target is null)
            return FunctionResult.FailureResult(startingUrl is null
                ? "Provide a startingUrl for the browser, open a webpage in the built-in browser, or select a desktop window."
                : "startingUrl must be a complete HTTP or HTTPS address without embedded credentials.");
        try
        {
            return await service.RunAsync(target, instruction, ToolExecutionContext.CurrentCancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            return FunctionResult.FailureResult(exception.Message);
        }
    }
}
