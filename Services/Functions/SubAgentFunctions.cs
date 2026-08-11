using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Athena.UI.Services.SubAgents;
using Serilog;
using System.Linq;
using System.Threading.Tasks;

namespace Athena.UI.Services.Functions;

/// <summary>dispatch_subagents 工具的实现：把一批独立子任务交给编排器并行执行。</summary>
public class SubAgentFunctions
{
    private readonly ISubAgentOrchestrator _orchestrator;
    private readonly ILogger _logger;

    public SubAgentFunctions(ISubAgentOrchestrator orchestrator, ILogger logger)
    {
        _orchestrator = orchestrator;
        _logger = logger.ForContext<SubAgentFunctions>();
    }

    public async Task<FunctionResult> DispatchSubagentsAsync(SubAgentTaskInput[] tasks)
    {
        if (tasks == null || tasks.Length < 2)
        {
            return FunctionResult.FailureResult("dispatch_subagents requires at least 2 independent tasks; complete a single task directly.");
        }
        if (tasks.Length > 8)
            return FunctionResult.FailureResult("dispatch_subagents accepts at most 8 tasks per batch.");
        if (tasks.Any(task => string.IsNullOrWhiteSpace(task.Title) || string.IsNullOrWhiteSpace(task.Instruction)))
            return FunctionResult.FailureResult("Every sub-agent task requires a non-empty title and instruction.");

        // 取消令牌经 AsyncLocal 由主对话循环传入，让"停止"能终止整批子代理。
        var token = ToolExecutionContext.CurrentCancellationToken;
        _logger.Information("dispatch_subagents invoked with {Count} task(s)", tasks.Length);

        var combined = await _orchestrator.DispatchBatchAsync(tasks, token);
        return FunctionResult.SuccessResult(combined);
    }
}
