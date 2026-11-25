using Athena.UI.Services.ComputerUse;
using Athena.UI.Services.Interfaces;
using Athena.UI.Services.SubAgents;
using System;
using System.Threading.Tasks;

namespace Athena.UI.Services.Functions;

public sealed class ComputerUseFunctions(ComputerUseService service)
{
    public bool IsAvailable => service.IsAvailable;

    public async Task<FunctionResult> RunComputerTaskAsync(string instruction)
    {
        if (ToolApprovalContext.CurrentMode != ToolApprovalContext.ExecutionMode.Interactive)
            return FunctionResult.FailureResult("Computer Use requires an interactive conversation.");
        if (string.IsNullOrWhiteSpace(instruction))
            return FunctionResult.FailureResult("A Computer Use instruction is required.");
        if (service.SelectedTarget is not { } target)
            return FunctionResult.FailureResult("Select the target window in Athena's Computer Use panel before starting.");
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
