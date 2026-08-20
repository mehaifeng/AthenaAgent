using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Athena.UI.Services.SubAgents;
using Serilog;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Athena.UI.Services.Functions;

public class BrowserTaskFunctions
{
    private readonly IBrowserAgentService _browserAgentService;
    private readonly ILogger _logger;

    public BrowserTaskFunctions(IBrowserAgentService browserAgentService, ILogger logger)
    {
        _browserAgentService = browserAgentService;
        _logger = logger.ForContext<BrowserTaskFunctions>();
    }

    public async Task<FunctionResult> RunBrowserTaskAsync(string instruction, string? startUrl = null, int? maxSteps = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(instruction))
            {
                return FunctionResult.FailureResult("Browser task instruction cannot be empty.");
            }

            // 读取主对话循环经 AsyncLocal 透传的取消令牌，使用户点"停止"能中断浏览器智能体的多步循环。
            var cancellationToken = ToolExecutionContext.CurrentCancellationToken;

            var result = await _browserAgentService.RunTaskAsync(new BrowserTaskRequest
            {
                Instruction = instruction,
                StartUrl = startUrl,
                MaxSteps = maxSteps,
                CloseSessionOnCompletion = true
            }, cancellationToken);

            _logger.Information("Browser task finished. Success={Success}, Actions={Actions}, Url={Url}",
                result.Success, result.ActionsTakenCount, result.FinalUrl);

            var compactData = new
            {
                summary = result.Summary,
                finalUrl = result.FinalUrl,
                annotatedScreenshotPath = result.FinalObservation?.AnnotatedScreenshotPath,
                evidence = result.Evidence.TakeLast(16).ToList(),
                actionsTakenCount = result.ActionsTakenCount,
                completionStatus = result.CompletionStatus.ToString(),
                requiresUserInput = result.RequiresUserInput,
                error = result.Error,
                markedElements = result.FinalObservation?.Elements.Select(e => new
                {
                    id = e.ElementId,
                    index = e.Index,
                    tag = e.TagName,
                    role = e.Role,
                    text = e.Text,
                    ariaLabel = e.AriaLabel,
                    placeholder = e.Placeholder,
                    href = e.Href,
                    inputType = e.InputType,
                    value = e.IsSensitive ? null : e.Value,
                    isChecked = e.IsChecked
                }).Take(20).ToList()
            };

            if (result.Success)
            {
                return FunctionResult.SuccessResult("Browser task completed.", compactData);
            }

            return new FunctionResult
            {
                Success = false,
                Message = result.Error ?? result.Summary,
                Data = compactData
            };
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Browser task cancelled by user stop request.");
            return FunctionResult.FailureResult("Browser task was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Browser task function failed");
            return FunctionResult.FailureResult($"Browser task failed: {ex.Message}");
        }
    }
}
