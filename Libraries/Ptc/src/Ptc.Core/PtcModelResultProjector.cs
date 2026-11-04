using System.Text;
using System.Text.Json;

namespace Ptc.Core;

/// <summary>Only this whitelist, never the execution result/records, is suitable for a model tool result.</summary>
public static class PtcModelResultProjector
{
    public static string Project(PtcExecutionResult result, int maxBytes, string? traceRef = null)
    {
        if (maxBytes < 256) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var json = JsonSerializer.Serialize(new
        {
            runId = result.Context.RunId,
            status = JsonNamingPolicy.CamelCase.ConvertName(result.Status.ToString()),
            output = result.Output,
            metrics = new { result.Metrics.CallsAttempted, result.Metrics.CallsStarted, result.Metrics.CallsFinished },
            artifactIds = result.Artifacts.Select(item => item.Id).ToArray(),
            traceRef,
            failure = result.Failure,
            isolation = "inProcess"
        }, PtcJson.Options);
        if (Encoding.UTF8.GetByteCount(json) <= maxBytes) return json;
        // This deliberately drops all optional fields, rather than slicing serialized JSON.
        return "{\"status\":\"budgetExceeded\",\"failure\":{\"code\":\"model_output_limit_exceeded\",\"message\":\"Model result exceeds its byte limit.\"},\"isolation\":\"inProcess\"}";
    }
}
