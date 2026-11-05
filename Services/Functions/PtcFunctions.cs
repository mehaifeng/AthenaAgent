using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Services.Interfaces;
using Athena.UI.Services.Ptc;
using Athena.UI.Services.SubAgents;
using Ptc.Core;
using Serilog;

namespace Athena.UI.Services.Functions;

public sealed class PtcFunctions
{
    public const string Description = """
        Runs a bounded JavaScript async FUNCTION BODY to orchestrate the current read-only tool grant.
        Optional inputs must be a JSON object. Do not write an IIFE. Use tools.list() for authorized names
        and short descriptions, tools.describe(name) for its inputSchema (null if unavailable), and
        await tools.call(name,args) for {success,message,data,errorCode,artifacts}. Read business data
        from result.data, not directly from result. Conditions, loops, Promise.all and Promise.allSettled
        are supported. Emit only useful aggregates via text(value); return only ends execution and
        produces no model output. Await EVERY call; unawaited work is cancelled, not backgrounded.
        V0 permits only read_system_file/get_file_info/search_in_file/search_in_directory/list_system_directory,
        intersected with this turn's host snapshot. No filesystem/network/CLR globals, eval or Function
        constructors. No recursive execution, terminal, MCP, browser, cron, configuration or subagents.
        Calls still undergo normal per-tool approval and file quotas. Budgets and Stop cannot be caught
        to resume a run. Data must be losslessly JSON encodable; use strings for integers above 2^53-1.
        Example: const r=await tools.call('list_system_directory',{path:inputs.path});
        if(r.success) text({count:r.data.entries.length}); else text({error:r.errorCode});
        Execution is in-process, NOT a strong sandbox. Raw records are host-owned; only explicit output
        and an opaque traceRef return to the model. Never retry an executed program automatically.
        """;

    private readonly IPtcExecutor _executor;
    private readonly IConfigService _config;
    private readonly Func<IFunctionRegistry> _registry;
    private readonly IConversationSessionAccessor _sessions;
    private readonly IFileSystemService _files;
    private readonly IPlatformPathService _paths;
    private readonly AthenaPtcRunStore _store;
    private readonly ILogger _logger;

    public PtcFunctions(IPtcExecutor executor, IConfigService config, Func<IFunctionRegistry> registry,
        IConversationSessionAccessor sessions, IFileSystemService files, IPlatformPathService paths,
        AthenaPtcRunStore store, ILogger logger)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger.ForContext<PtcFunctions>();
    }

    public FunctionResult? RejectUnavailable()
    {
        var invocation = AthenaPtcInvocationScope.Current;
        if (_config.Load().PtcEnabled && invocation?.Mode == ToolApprovalContext.ExecutionMode.Interactive
            && ToolApprovalContext.CurrentMode == ToolApprovalContext.ExecutionMode.Interactive && !AthenaPtcInvocationScope.IsChild)
            return null;
        return Failure("tool_not_available", "PTC is disabled or this execution path has no interactive PTC grant.");
    }

    public async Task<FunctionResult> ExecuteProgramAsync(string code, JsonElement? inputs = null)
    {
        if (RejectUnavailable() is { } unavailable) return unavailable;
        var invocation = AthenaPtcInvocationScope.Current!;
        var cancellationToken = ToolExecutionContext.CurrentCancellationToken;
        try
        {
            using var reservation = await _store.ReserveAsync(cancellationToken).ConfigureAwait(false);
            var context = new PtcRunContext(reservation.RunId, invocation.ParentToolCallId,
                invocation.ConversationId, invocation.WorkspaceId, invocation.DelegatedTask);
            var host = new AthenaPtcToolHost(invocation, context, _registry(), _config, _sessions, _files, _paths);
            // UTF-8 bytes >= UTF-16 chars for the valid JSON boundary. Reserve outer envelope space
            // conservatively, without a token estimator or disabling the Registry context budget.
            var ordinaryBudget = _config.Load().MaxToolResultChars;
            var modelBytes = ordinaryBudget > 0 ? Math.Clamp(ordinaryBudget - 512, 256, 24 * 1024) : 24 * 1024;
            var limits = new PtcExecutionLimits
            {
                MaxModelResultBytes = modelBytes,
                MaxOutputBytes = Math.Min(16 * 1024, Math.Max(1, modelBytes - 1024)),
                MaxConcurrency = Math.Clamp(_config.Load().MaxParallelToolCalls, 1, 4)
            };
            var request = new PtcExecutionRequest(code, inputs ?? PtcJson.Parse("{}"),
                host.Tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal), context, limits);
            var result = await _executor.ExecuteAsync(request, host, new AthenaPtcEventAdapter(invocation), cancellationToken).ConfigureAwait(false);
            string? traceRef = null;
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                traceRef = await _store.SaveAsync(reservation, code, result, cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "PTC trace save failed for {RunId}; no nonexistent trace reference will be returned", context.RunId);
                result = result with { Status = PtcRunStatus.Failed, Failure = new("trace_store_failed", "Execution finished but its audit record could not be saved.") };
            }
            var projection = PtcModelResultProjector.Project(result, modelBytes, traceRef);
            var data = PtcJson.Parse(projection, modelBytes, 40);
            var attachments = host.GetAttachments(result.Artifacts);
            return result.Status == PtcRunStatus.Completed
                ? FunctionResult.SuccessResult("PTC completed.", data, attachments)
                : FunctionResult.FailureResult("PTC stopped: " + result.Failure?.Code, data, attachments);
        }
        catch (PtcRuntimeException ex) { return Failure(ex.Code, ex.Message); }
        catch (OperationCanceledException) { return Failure("cancelled", "PTC execution was cancelled."); }
        catch (Exception ex)
        {
            _logger.Warning(ex, "PTC host execution failed");
            return Failure("host_error", "PTC host execution failed. No program replay was attempted.");
        }
    }

    private static FunctionResult Failure(string code, string message) => new()
    { Success = false, Message = message, ErrorCode = code, Data = new { failure = new { code, message }, isolation = "inProcess" } };
}
