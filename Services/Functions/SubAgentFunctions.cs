using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Athena.UI.Services.SubAgents;
using Serilog;
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
        if (tasks == null || tasks.Length == 0)
        {
            return FunctionResult.FailureResult("Provide at least one task in 'tasks'.");
        }

        // 取消令牌经 AsyncLocal 由主对话循环传入，让"停止"能终止整批子代理。
        var token = ToolExecutionContext.CurrentCancellationToken;
        _logger.Information("dispatch_subagents invoked with {Count} task(s)", tasks.Length);

        var combined = await _orchestrator.DispatchBatchAsync(tasks, token);
        return FunctionResult.SuccessResult(combined);
    }
}
