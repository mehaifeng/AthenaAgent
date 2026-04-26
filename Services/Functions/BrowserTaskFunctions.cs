using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
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

            var result = await _browserAgentService.RunTaskAsync(new BrowserTaskRequest
            {
                Instruction = instruction,
                StartUrl = startUrl,
                MaxSteps = maxSteps,
                CloseSessionOnCompletion = true
            });

            _logger.Information("Browser task finished. Success={Success}, Actions={Actions}, Url={Url}",
                result.Success, result.ActionsTakenCount, result.FinalUrl);

            var compactData = new
            {
                summary = result.Summary,
                finalUrl = result.FinalUrl,
                annotatedScreenshotPath = result.FinalObservation?.AnnotatedScreenshotPath,
                evidence = result.Evidence.Take(8).ToList(),
                actionsTakenCount = result.ActionsTakenCount,
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
                    href = e.Href
                }).Take(20).ToList()
            };

            return result.Success
                ? FunctionResult.SuccessResult("Browser task completed.", compactData)
                : FunctionResult.FailureResult(result.Error ?? result.Summary);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Browser task function failed");
            return FunctionResult.FailureResult($"Browser task failed: {ex.Message}");
        }
    }
}
