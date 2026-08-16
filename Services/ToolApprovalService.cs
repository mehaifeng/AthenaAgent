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
        ConversationExecutionCoordinator? executionCoordinator = null)
    {
        _configService = configService;
        _prompter = prompter;
        _sessionAccessor = sessionAccessor;
        _aiEvaluator = aiEvaluator;
        _localizationService = localizationService;
        _logger = logger.ForContext<ToolApprovalService>();
        _executionCoordinator = executionCoordinator;
    }

    public async Task<ToolApprovalDecision> EvaluateAsync(string functionName, string argumentsJson, CancellationToken cancellationToken)
    {
        var (risk, riskReason) = ToolRiskClassifier.Classify(functionName, argumentsJson);
        var config = _configService.Load();
        var mode = config.ToolApprovalMode;
        var execMode = ToolApprovalContext.CurrentMode;

        // 审批键带作用域：终端按命令名、下载按主机名、写文件按目标目录聚合。
        // 「始终允许」因此不再等于全盘放行（见 ToolApprovalKey）。
        var keyInfo = ToolApprovalKey.Build(functionName, argumentsJson);
        var isTerminal = keyInfo.IsTerminal;
        var commandName = keyInfo.CommandName;
        var approvalKey = keyInfo.Key;
        // 会话放行键含当前对话 ID：不同对话互不影响。
        var sessionKey = BuildSessionKey(approvalKey);

        _logger.Debug(
            "ToolApproval evaluation started: Function={Function}, Risk={Risk}, Reason={Reason}, Mode={Mode}, ExecMode={ExecMode}",
            functionName, risk, riskReason ?? "(none)", mode, execMode);

        var decision = Decide(functionName, argumentsJson, risk, riskReason, mode, execMode, config, isTerminal, commandName, approvalKey, sessionKey);

        // 交互模式下命中「需询问」分支会返回 null，交由弹窗流程处理。
        if (decision != null)
        {
            Audit(functionName, risk, execMode, decision);
            return decision;
        }

        var request = BuildRequest(functionName, argumentsJson, risk, riskReason, isTerminal, keyInfo);

        if (mode == ToolApprovalMode.Automatic)
        {
            var automatic = _aiEvaluator == null
                ? ToolApprovalDecision.Deny("自动审批模型不可用")
                : await _aiEvaluator.EvaluateAsync(request, cancellationToken);
            _logger.Information(
                "ToolApproval automatic-mode AI evaluation completed: Function={Function}, Approved={Approved}, Reason={Reason}",
                functionName, automatic.Approved, automatic.Reason);
            Audit(functionName, risk, execMode, automatic);
            return automatic;
        }

        // —— 需要人工确认，但非交互式路径不能弹窗 ——
        if (execMode != ToolApprovalContext.ExecutionMode.Interactive || _prompter == null)
        {
            var nonInteractive = ResolveNonInteractive(functionName, risk, execMode, config);
            _logger.Information(
                "ToolApproval non-interactive path decision: Function={Function}, Risk={Risk}, ExecMode={ExecMode}, Approved={Approved}, Reason={Reason}",
                functionName, risk, execMode, nonInteractive.Approved, nonInteractive.Reason);
            Audit(functionName, risk, execMode, nonInteractive);
            return nonInteractive;
        }

        ToolApprovalScope scope;
        try
        {
            scope = _executionCoordinator == null
                ? await _prompter.PromptAsync(request, cancellationToken)
                : await _executionCoordinator.RunWithoutModelSlotAsync(
                    _sessionAccessor?.CurrentConversationId,
                    () => _prompter.PromptAsync(request, cancellationToken),
                    cancellationToken);
        }
        catch (OperationCanceledException)
        {
            var cancelled = ToolApprovalDecision.Deny("用户取消了本轮回复");
            _logger.Information("ToolApproval prompt cancelled by user: Function={Function}", functionName);
            Audit(functionName, risk, execMode, cancelled);
            return cancelled;
        }

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
                functionName, approvalKey, isTerminal);
            await PersistAlwaysAllowAsync(isTerminal, commandName, approvalKey);
        }

        var userDecision = new ToolApprovalDecision
        {
            Scope = scope,
            Reason = $"用户弹窗决策：{scope}"
        };
        Audit(functionName, risk, execMode, userDecision);
        return userDecision;
    }

    /// <summary>
    /// 无需弹窗即可裁决的分支返回具体决策；返回 null 表示「需人工确认」。
    /// </summary>
    private ToolApprovalDecision? Decide(
        string functionName, string argumentsJson, ToolRisk risk, string? riskReason,
        ToolApprovalMode mode, ToolApprovalContext.ExecutionMode execMode, AppConfig config,
        bool isTerminal, string? commandName, string approvalKey, string sessionKey)
    {
        // 受信任的第一方例程（知识库定期整理）：自动放行，避免破坏例程功能。
        if (execMode == ToolApprovalContext.ExecutionMode.Trusted)
        {
            _logger.Debug("ToolApproval trusted background routine auto-allow: Function={Function}", functionName);
            return ToolApprovalDecision.AllowOnce("受信任的后台例程，自动放行");
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

        // 用户已永久放行。同时匹配带作用域的新键与裸函数名——后者是本次改动之前写下的
        // 全局放行记录，用户当初是明确选择的，不能因为键的形态变了就悄悄失效。
        if (config.AutoAllowedTools.Contains(approvalKey, StringComparer.OrdinalIgnoreCase))
        {
            _logger.Debug("ToolApproval hit permanent allowlist: Key={Key}", approvalKey);
            return ToolApprovalDecision.AllowOnce("命中永久放行清单");
        }
        if (!isTerminal && config.AutoAllowedTools.Contains(functionName, StringComparer.OrdinalIgnoreCase))
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
            if (_sessionAllowed.Contains(sessionKey))
            {
                _logger.Debug("ToolApproval session-scoped allow hit: SessionKey={SessionKey}", sessionKey);
                return ToolApprovalDecision.AllowOnce("本对话内已放行");
            }
        }

        // 需要人工确认 → 交给上层弹窗 / 无人值守策略。
        return null;
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

    private ToolApprovalRequest BuildRequest(
        string functionName, string argumentsJson, ToolRisk risk, string? riskReason,
        bool isTerminal, ToolApprovalKey.Result keyInfo)
    {
        return new ToolApprovalRequest
        {
            FunctionName = functionName,
            Risk = risk,
            Summary = ToolCallDisplay.Summarize(functionName, argumentsJson, _localizationService),
            PrettyArguments = ToolCallDisplay.PrettyArguments(argumentsJson),
            CommandLine = isTerminal ? TerminalCommandRisk.BuildCommandLine(argumentsJson) : null,
            RiskReason = riskReason,
            ApprovalKey = keyInfo.Key,
            ApprovalScope = keyInfo.Scope
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

    private void Audit(string functionName, ToolRisk risk, ToolApprovalContext.ExecutionMode execMode, ToolApprovalDecision decision)
    {
        // 审计日志：谁（模式）、放行/拒绝、工具、风险、原因。便于事后追溯。
        _logger.Information(
            "ToolApproval | function={Function} risk={Risk} exec={ExecMode} decision={Scope} approved={Approved} reason={Reason}",
            functionName, risk, execMode, decision.Scope, decision.Approved, decision.Reason);
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
