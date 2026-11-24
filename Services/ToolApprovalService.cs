using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;

namespace Athena.UI.Services;

/// <summary>
/// 工具审批服务实现：风险分级 + 配置/白名单/会话放行判定 + 交互/无人值守裁决 + 审计日志。
/// 单例。会话放行集合是内存态，随进程生命周期存在；永久放行写入配置。
/// </summary>
public class ToolApprovalService : IToolApprovalService
{
    private readonly IConfigService _configService;
    private readonly IToolApprovalPrompter? _prompter;
    private readonly IConversationSessionAccessor? _sessionAccessor;
    private readonly IAiToolApprovalEvaluator? _aiEvaluator;
    private readonly ILocalizationService? _localizationService;
    private readonly ILogger _logger;
    private readonly ConversationExecutionCoordinator? _executionCoordinator;
    private readonly IPlatformPathService? _paths;

    // 会话内「始终允许」的去重键（AllowForSession）。键含当前对话 ID，做到每个对话独立——
    // 新开对话不会继承上一对话的会话放行。终端命令按命令名聚合。
    private readonly HashSet<string> _sessionAllowed = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sessionLock = new();

    public ToolApprovalService(
        IConfigService configService,
        IToolApprovalPrompter? prompter,
        ILogger logger,
        IConversationSessionAccessor? sessionAccessor = null,
        IAiToolApprovalEvaluator? aiEvaluator = null,
        ILocalizationService? localizationService = null,
        ConversationExecutionCoordinator? executionCoordinator = null,
        IPlatformPathService? paths = null)
    {
        _configService = configService;
        _prompter = prompter;
        _sessionAccessor = sessionAccessor;
        _aiEvaluator = aiEvaluator;
        _localizationService = localizationService;
        _logger = logger.ForContext<ToolApprovalService>();
        _executionCoordinator = executionCoordinator;
        _paths = paths;
    }

    public async Task<ToolApprovalDecision> EvaluateAsync(
        string functionName, string argumentsJson, CancellationToken cancellationToken, string? toolDescription = null)
    {
        var config = _configService.Load();
        var mode = config.ToolApprovalMode;
        var execMode = ToolApprovalContext.CurrentMode;
        if (mode == ToolApprovalMode.Automatic && execMode != ToolApprovalContext.ExecutionMode.Trusted)
            return await EvaluateAutomaticallyAsync(functionName, argumentsJson, toolDescription, execMode, cancellationToken);

        // 只读终端命令碰到文件工具的读黑名单或 config.json 时不再免审批。名单取自当前配置，不另写一份。
        var (risk, riskReason) = ToolRiskClassifier.Classify(
            functionName, argumentsJson, SensitiveLocations.From(config.FileSystemPolicy, _configService.ConfigFilePath));
        // The existing manual approval modes also present scheduled calls in their conversation.
        if (execMode == ToolApprovalContext.ExecutionMode.Scheduled)
            execMode = ToolApprovalContext.ExecutionMode.Interactive;

        if (cancellationToken.IsCancellationRequested)
        {
            var cancelled = ToolApprovalDecision.Cancelled("本轮已取消");
            Audit(functionName, risk, execMode, cancelled);
            return cancelled;
        }

        // 审批键带作用域：终端按命令名、下载按主机名、写文件按目标目录聚合。
        // 「始终允许」因此不再等于全盘放行（见 ToolApprovalKey）。
        var keyInfo = ToolApprovalKey.Build(functionName, argumentsJson);

        // 会话放行键含当前对话 ID：不同对话互不影响。
        var sessionKey = BuildSessionKey(keyInfo.Key);

        _logger.Debug(
            "ToolApproval evaluation started: Function={Function}, Risk={Risk}, Reason={Reason}, Mode={Mode}, ExecMode={ExecMode}",
            functionName, risk, riskReason ?? "(none)", mode, execMode);

        var decision = DecideLocally(functionName, risk, mode, execMode, config, keyInfo, sessionKey);
        if (decision == null && !CanPrompt(execMode))
            decision = ResolveNonInteractive(functionName, risk, execMode, config);
        if (decision != null)
        {
            Audit(functionName, risk, execMode, decision);
            return decision;
        }

        var request = BuildRequest(functionName, argumentsJson, risk, riskReason, keyInfo);
        return await PromptAsync(request, execMode, cancellationToken, keyInfo, sessionKey);
    }

    private async Task<ToolApprovalDecision> EvaluateAutomaticallyAsync(
        string functionName, string argumentsJson, string? toolDescription,
        ToolApprovalContext.ExecutionMode execMode, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            var cancelled = ToolApprovalDecision.Cancelled("本轮已取消");
            Audit(functionName, null, execMode, cancelled);
            return cancelled;
        }

        var request = BuildRequest(functionName, argumentsJson, toolDescription: toolDescription);
        ToolApprovalDecision review;
        if (_aiEvaluator == null)
        {
            _logger.Warning("Automatic approval model is unavailable for {Function}", functionName);
            review = ToolApprovalDecision.JudgeFailed(ToolApprovalJudgeFailureKind.Configuration, "没有可用的自动审批模型");
        }
        else
        {
            try
            {
                review = await _aiEvaluator.EvaluateAsync(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                review = ToolApprovalDecision.Cancelled("本轮已取消");
            }
            catch (Exception ex)
            {
                _logger.Warning("Approval reviewer could not complete for {Function}: {ErrorType}", functionName, ex.GetType().Name);
                review = ToolApprovalDecision.JudgeFailed(ToolApprovalJudgeFailureKind.Transport, "自动评审未能完成");
            }
        }
        if (cancellationToken.IsCancellationRequested)
            review = ToolApprovalDecision.Cancelled("本轮已取消");
        _logger.Information("ToolApproval review: Function={Function}, Source={Source}, FailureKind={FailureKind}, Approved={Approved}",
            functionName, review.Source, review.FailureKind, review.Approved);

        if (CanPrompt(execMode) && review.Source is (ToolApprovalSource.JudgeNeedsUser or ToolApprovalSource.JudgeFailure))
        {
            request.ReviewReason = review.ReviewDetail;
            request.ReviewRisk = review.ReviewRisk;
            return await PromptAsync(request, execMode, cancellationToken);
        }

        Audit(functionName, null, execMode, review);
        return review;
    }

    private bool CanPrompt(ToolApprovalContext.ExecutionMode execMode) =>
        execMode == ToolApprovalContext.ExecutionMode.Interactive && _prompter != null;

    private async Task<ToolApprovalDecision> PromptAsync(
        ToolApprovalRequest request, ToolApprovalContext.ExecutionMode execMode, CancellationToken cancellationToken,
        ToolApprovalKey.Result keyInfo = default, string sessionKey = "")
    {
        var functionName = request.FunctionName;
        var risk = request.Risk;
        ToolApprovalScope scope;
        try
        {
            scope = _executionCoordinator == null
                ? await _prompter!.PromptAsync(request, cancellationToken)
                : await _executionCoordinator.RunWithoutModelSlotAsync(
                    _sessionAccessor?.CurrentConversationId,
                    () => _prompter!.PromptAsync(request, cancellationToken),
                    cancellationToken);
        }
        catch (OperationCanceledException)
        {
            var cancelled = ToolApprovalDecision.Cancelled("用户取消了本轮回复");
            _logger.Information("ToolApproval prompt cancelled by user: Function={Function}", functionName);
            Audit(functionName, risk, execMode, cancelled);
            return cancelled;
        }
        if (cancellationToken.IsCancellationRequested)
        {
            var cancelled = ToolApprovalDecision.Cancelled("本轮已取消");
            Audit(functionName, risk, execMode, cancelled);
            return cancelled;
        }
        if (!request.AllowsPersistentApproval && scope is ToolApprovalScope.AllowForSession or ToolApprovalScope.AllowAlways)
            scope = ToolApprovalScope.AllowOnce;

        // 记忆用户的放行选择。
        if (scope == ToolApprovalScope.AllowForSession)
        {
            lock (_sessionLock)
            {
                _sessionAllowed.Add(sessionKey);
            }
            _logger.Information(
                "ToolApproval user chose AllowForSession: Function={Function}, SessionKey={SessionKey}",
                functionName, sessionKey);
        }
        else if (scope == ToolApprovalScope.AllowAlways)
        {
            _logger.Information(
                "ToolApproval user chose AllowAlways: Function={Function}, Key={Key}, IsTerminal={IsTerminal}",
                functionName, keyInfo.Key, keyInfo.IsTerminal);
            await PersistAlwaysAllowAsync(keyInfo.IsTerminal, keyInfo.CommandName, keyInfo.Key);
        }

        var userDecision = new ToolApprovalDecision
        {
            Scope = scope,
            Source = ToolApprovalSource.User,
            Reason = $"用户弹窗决策：{scope}",
            ReviewRisk = request.ReviewRisk
        };
        Audit(functionName, risk, execMode, userDecision);
        return userDecision;
    }

    /// <summary>
    /// 无需弹窗即可裁决的分支返回具体决策；返回 null 表示「需人工确认」。
    /// </summary>
    private ToolApprovalDecision? DecideLocally(
        string functionName, ToolRisk risk, ToolApprovalMode mode, ToolApprovalContext.ExecutionMode execMode,
        AppConfig config, ToolApprovalKey.Result keyInfo, string sessionKey)
    {
        // 受信任的第一方例程（知识库定期整理）：只对授权里的工具自动放行，结论在这里定死——
        // Off 不放宽它，Automatic 也不把授权外的调用转交审批模型。
        if (execMode == ToolApprovalContext.ExecutionMode.Trusted)
        {
            return DecideTrusted(functionName);
        }

        // 全局关闭：一切自动放行。
        if (mode == ToolApprovalMode.Off)
        {
            _logger.Debug("ToolApproval mode=Off auto-allow: Function={Function}", functionName);
            return ToolApprovalDecision.AllowOnce("审批模式=Off，全部放行");
        }

        // 只读工具在非严格模式下自动放行。
        if (risk == ToolRisk.ReadOnly && mode != ToolApprovalMode.Strict)
        {
            _logger.Debug("ToolApproval read-only auto-allow: Function={Function}, Mode={Mode}", functionName, mode);
            return ToolApprovalDecision.AllowOnce("只读工具自动放行");
        }

        // 只增不改（写入路径必须尚不存在，源文件原样保留）在非严格模式下同样自动放行：
        // 它毁不掉任何既有内容，而逐次弹窗会把「做份 PPT」这类用户刚点名的任务切成 3~5 段。
        if (risk == ToolRisk.AdditiveWrite && mode != ToolApprovalMode.Strict)
        {
            _logger.Debug("ToolApproval additive-write auto-allow: Function={Function}, Mode={Mode}", functionName, mode);
            return ToolApprovalDecision.AllowOnce("只增不改的写入自动放行（目标已存在时工具自身会失败）");
        }

        var (approvalKey, _, commandName) = keyInfo;
        var isTerminal = keyInfo.IsTerminal;
        // 用户已永久放行。同时匹配带作用域的新键与裸函数名——后者是本次改动之前写下的
        // 全局放行记录，用户当初是明确选择的，不能因为键的形态变了就悄悄失效。
        var comparer = StringComparer.OrdinalIgnoreCase;
        if (config.AutoAllowedTools.Contains(approvalKey, comparer))
        {
            _logger.Debug("ToolApproval hit permanent allowlist: Key={Key}", approvalKey);
            return ToolApprovalDecision.AllowOnce("命中永久放行清单");
        }
        if (!isTerminal && config.AutoAllowedTools.Contains(functionName, comparer))
        {
            _logger.Debug("ToolApproval hit legacy unscoped allowlist entry: Function={Function}", functionName);
            return ToolApprovalDecision.AllowOnce("命中永久放行清单（历史的全局条目）");
        }

        // 终端命令命中用户白名单命令。
        if (isTerminal && !string.IsNullOrEmpty(commandName)
            && config.TerminalAllowlist.Contains(commandName, StringComparer.OrdinalIgnoreCase))
        {
            _logger.Debug("ToolApproval terminal command in whitelist: Command={Command}", commandName);
            return ToolApprovalDecision.AllowOnce($"终端命令 '{commandName}' 在白名单中");
        }

        // 当前对话内已放行。
        lock (_sessionLock)
        {
            if (_sessionAllowed.Contains(sessionKey, comparer))
            {
                _logger.Debug("ToolApproval session-scoped allow hit: SessionKey={SessionKey}", sessionKey);
                return ToolApprovalDecision.AllowOnce("本对话内已放行");
            }
        }

        // 需要人工确认 → 交给上层弹窗 / 无人值守策略。
        return null;
    }

    /// <summary>
    /// 受信任例程的裁决。此前这里不看工具名就放行，而 ExecuteAsync 只按名字找执行器、
    /// 不核对调用方声明过哪些工具：整理模型输出 execute_terminal_command 或
    /// modify_self_configuration（能把审批模式写成 Off）都会被直接执行。
    /// 路径边界不在这里判断：FileSystemService 在解析完路径之后按同一份授权的目录执行。
    /// </summary>
    private ToolApprovalDecision DecideTrusted(string functionName)
    {
        var grant = ToolApprovalContext.CurrentTrustedGrant;
        if (grant == null)
        {
            // EnterTrusted 只接受带授权的入口；走到这里说明作用域被绕开了，拒绝并留痕。
            _logger.Error("ToolApproval trusted scope carries no grant; denying {Function}", functionName);
            return ToolApprovalDecision.Deny("受信任作用域缺少授权，默认拒绝");
        }

        // 只能由人批准的调用不会因为「受信任」就改由例程自己放行——与其它无人值守路径同一条规则。
        if (ToolRiskClassifier.IsNeverUnattended(functionName))
        {
            return ToolApprovalDecision.Deny($"'{functionName}' 只能由用户本人批准，后台例程（{grant.Routine}）一律拒绝");
        }

        if (!grant.Allows(functionName))
        {
            return ToolApprovalDecision.Deny(
                $"'{functionName}' 不在后台例程（{grant.Routine}）的工具授权内；可用工具：{string.Join(", ", grant.ToolNames)}");
        }

        _logger.Debug("ToolApproval trusted routine auto-allow: Routine={Routine}, Function={Function}", grant.Routine, functionName);
        return ToolApprovalDecision.AllowOnce($"受信任的后台例程（{grant.Routine}），授权内工具自动放行");
    }

    /// <summary>
    /// 非交互路径（子代理 / 未设置作用域）的静态裁决：
    /// - 破坏性：一律拒绝（后台绝不无人删档 / 跑高危命令）。
    /// - 自我配置 / MCP 服务器增删：一律拒绝，且不受「子代理继承审批」影响。
    /// - 只增不改：放行（毁不掉既有内容，且生成文件正是子代理的常规产出）。
    /// - 敏感：子代理在 SubAgentsInheritApproval=true 时放行，否则拒绝。
    /// - 其余（未知模式 / 无 prompter 的交互）：fail-safe 拒绝。
    /// </summary>
    private static ToolApprovalDecision ResolveNonInteractive(
        string functionName, ToolRisk risk, ToolApprovalContext.ExecutionMode execMode, AppConfig config)
    {
        if (risk == ToolRisk.Destructive)
        {
            return ToolApprovalDecision.Deny("无人值守执行，破坏性工具一律拒绝");
        }

        // 「继承审批」本意是让子代理能干活，不是把应用自身的能力边界一并交出去。
        // modify_self_configuration 可写 Security.ToolApprovalMode——放行它等于允许
        // 后台例程自己关掉审批闸门，因此这条名单不受任何开关影响。
        if (ToolRiskClassifier.IsNeverUnattended(functionName))
        {
            return ToolApprovalDecision.Deny(
                $"'{functionName}' 会改变应用自身的能力边界，无人值守路径一律拒绝（与「子代理继承审批」无关）");
        }

        if (execMode == ToolApprovalContext.ExecutionMode.NonInteractive && risk == ToolRisk.AdditiveWrite)
        {
            return ToolApprovalDecision.AllowOnce("子代理：只增不改的写入放行");
        }

        if (execMode == ToolApprovalContext.ExecutionMode.NonInteractive
            && risk == ToolRisk.Sensitive
            && config.SubAgentsInheritApproval)
        {
            return ToolApprovalDecision.AllowOnce("子代理继承审批：敏感工具放行");
        }

        return ToolApprovalDecision.Deny(
            execMode == ToolApprovalContext.ExecutionMode.NonInteractive
                ? "子代理未获授权执行敏感工具（可在设置中开启「子代理继承审批」）"
                : "无审批交互通道，默认拒绝");
    }

    private string? ResolveFilePath(string path) => FileSystemService.ResolveApprovalPath(path, _paths);

    private ToolApprovalRequest BuildRequest(
        string functionName, string argumentsJson, ToolRisk? risk = null, string? riskReason = null,
        ToolApprovalKey.Result? keyInfo = null, string? toolDescription = null)
    {
        return new ToolApprovalRequest
        {
            FunctionName = functionName,
            ArgumentsJson = argumentsJson,
            ToolDescription = toolDescription,
            ResolveFilePath = ResolveFilePath,
            Risk = risk,
            Summary = ToolCallDisplay.Summarize(functionName, argumentsJson, _localizationService),
            PrettyArguments = ToolCallDisplay.PrettyArguments(argumentsJson),
            AllowsPersistentApproval = keyInfo.HasValue,
            CommandLine = string.Equals(functionName, "execute_terminal_command", StringComparison.OrdinalIgnoreCase)
                ? TerminalCommandRisk.BuildCommandLine(argumentsJson) : null,
            RiskReason = riskReason,
            ApprovalKey = keyInfo?.Key ?? string.Empty,
            ApprovalScope = keyInfo?.Scope
        };
    }

    /// <summary>
    /// 落库的是带作用域的审批键（如 write_system_file@/w/proj/src），不是裸函数名——
    /// 「始终允许」因此只覆盖用户当时真正看到的那一片。
    /// </summary>
    private async Task PersistAlwaysAllowAsync(bool isTerminal, string? commandName, string approvalKey)
    {
        try
        {
            var config = await _configService.LoadAsync();
            if (isTerminal && !string.IsNullOrEmpty(commandName))
            {
                if (!config.TerminalAllowlist.Contains(commandName, StringComparer.OrdinalIgnoreCase))
                {
                    config.TerminalAllowlist.Add(commandName);
                }
            }
            else if (!config.AutoAllowedTools.Contains(approvalKey, StringComparer.OrdinalIgnoreCase))
            {
                config.AutoAllowedTools.Add(approvalKey);
            }
            await _configService.SaveAsync(config);
            _logger.Information(
                "ToolApproval always-allow persisted: Key={Key}, IsTerminal={IsTerminal}, Command={Command}",
                approvalKey, isTerminal, commandName);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to persist always-allow: {Key}", approvalKey);
        }
    }

    private void Audit(string functionName, ToolRisk? risk, ToolApprovalContext.ExecutionMode execMode, ToolApprovalDecision decision)
    {
        // 审计日志：谁（模式）、放行/拒绝、工具、风险、原因。便于事后追溯。
        _logger.Information(
            "ToolApproval | function={Function} risk={Risk} exec={ExecMode} decision={Scope} approved={Approved} source={Source} failure={FailureKind} reason={Reason}",
            functionName, (object?)risk ?? decision.ReviewRisk, execMode, decision.Scope, decision.Approved,
            decision.Source, decision.FailureKind, decision.Reason);
    }

    /// <summary>
    /// 会话放行去重键 = 当前对话 ID + 审批键。做到「本会话允许」严格限定在当前对话，
    /// 新开对话即失效。无对话上下文时退化为固定前缀（不跨对话共享）。
    /// </summary>
    private string BuildSessionKey(string approvalKey)
    {
        var conversationId = _sessionAccessor?.CurrentConversationId ?? "(no-conversation)";
        return $"{conversationId}::{approvalKey}";
    }
}
