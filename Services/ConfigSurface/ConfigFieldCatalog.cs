using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.Services.ConfigSurface;

/// <summary>模型可感知配置字段的类型。coerce 层据此把字符串参数转换为目标类型。</summary>
public enum ConfigFieldType
{
    Boolean,
    Integer,
    Long,
    NullableLong,
    Number,
    String,
    Enum,
    StringList
}

/// <summary>
/// 声明式配置字段描述：view_self_configuration / modify_self_configuration 的单一事实来源。
/// Key 是面向模型稳定暴露的名字；Get/Set 直接落在 AppConfig 的强类型属性上（编译期校验，不玩反射）。
/// 派生数据（providers[].models 目录、ModelMetadata 快照等）永远不进字段表，只出现在 summary 的摘要里。
/// </summary>
public sealed class ConfigFieldDescriptor
{
    public required string Key { get; init; }

    public required string Section { get; init; }

    public required ConfigFieldType Type { get; init; }

    public required Func<AppConfig, object?> Get { get; init; }

    public required Action<AppConfig, object?> Set { get; init; }

    /// <summary>面向模型的英文说明（含取值范围 / 语义提示）。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>枚举名或受限字符串的合法取值（大小写不敏感）。</summary>
    public IReadOnlyList<string>? AllowedValues { get; init; }

    /// <summary>整数范围（闭区间；Max 为 null 表示无上限）。</summary>
    public (long Min, long? Max)? Range { get; init; }

    /// <summary>浮点范围（闭区间；Max 为 null 表示无上限），仅用于 Number 字段。</summary>
    public (double Min, double? Max)? NumberRange { get; init; }

    /// <summary>视图层脱敏（如 Token）。</summary>
    public bool Sensitive { get; init; }

    /// <summary>只读字段：视图可见、修改拒绝。</summary>
    public bool ReadOnly { get; init; }
}

/// <summary>
/// 字段目录。扩展配置时在此登记即可，视图与修改工具自动同步。
/// </summary>
public static class ConfigFieldCatalog
{
    /// <summary>展示顺序即配置面板的顺序。</summary>
    public static readonly IReadOnlyList<string> Sections =
    [
        "AI", "Context", "Appearance", "ChatAudio", "ImageGeneration", "WebSearch",
        "KnowledgeMaintenance", "Browser", "SubAgents", "DocumentParser", "Security",
        "Extensions", "Runtime"
    ];

    /// <summary>旧工具分区名 → 新分区名（向后兼容已学会旧分区名的模型）。</summary>
    private static readonly IReadOnlyDictionary<string, string> SectionAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Memory"] = "Context"
        };

    public static IReadOnlyList<ConfigFieldDescriptor> Fields { get; } = Build();

    public static bool TryGet(string key, out ConfigFieldDescriptor field)
    {
        field = Fields.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase))!;
        return field != null;
    }

    /// <summary>
    /// 把分区名（含别名、大小写不敏感）解析为规范名。
    /// 返回 true 且 resolved == null 表示「全部」（空 / "All" 字面量）；
    /// 返回 true 且 resolved 非空表示命中某分区；返回 false 表示未知分区。
    /// 注意：「全部」与「未知」必须区分——"All" 是 schema 中合法可传的值。
    /// </summary>
    public static bool TryResolveSection(string? section, out string? resolved)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(section) || string.Equals(section, "All", StringComparison.OrdinalIgnoreCase))
            return true;
        if (SectionAliases.TryGetValue(section, out var alias))
        {
            resolved = alias;
            return true;
        }
        var match = Sections.FirstOrDefault(candidate =>
            string.Equals(candidate, section, StringComparison.OrdinalIgnoreCase));
        if (match == null) return false;
        resolved = match;
        return true;
    }

    private static IReadOnlyList<ConfigFieldDescriptor> Build()
    {
        var fields = new List<ConfigFieldDescriptor>();
        fields.AddRange(RoleFields());
        fields.AddRange(
        [
            // —— AI ——
            Field("TopP", "AI", ConfigFieldType.Number,
                c => c.TopP, (c, v) => c.TopP = (double)v!,
                "Sampling temperature for the main conversation.", range: (0, 2)),
            Field("Timeout", "AI", ConfigFieldType.Integer,
                c => c.Timeout, (c, v) => c.Timeout = (int)v!,
                "HTTP timeout in seconds for model requests.", range: (5, 600)),
            Field("AI.MainConversationMaxParallel", "AI", ConfigFieldType.Integer,
                c => c.MainConversationMaxParallel, (c, v) => c.MainConversationMaxParallel = (int)v!,
                "Maximum concurrent main-conversation streams.", range: (1, 16)),
            Field("AI.MainConversationMaxIterations", "AI", ConfigFieldType.Integer,
                c => c.MainConversationMaxIterations, (c, v) => c.MainConversationMaxIterations = (int)v!,
                "Maximum tool-loop rounds in a single main-conversation reply. When the budget runs out the reply stops mid-task and the user must say \"continue\"; raise it for long multi-step skills.",
                range: (1, 200)),

            // —— Context（v6 起 ContextPolicy 是权威来源；顶层同名键为遗留镜像）——
            Field("ContextPolicy.Mode", "Context", ConfigFieldType.Enum,
                c => c.ContextPolicy.Mode, (c, v) => c.ContextPolicy.Mode = Enum.Parse<ContextPolicyMode>((string)v!, ignoreCase: true),
                "Context window policy: Auto derives the cap from model metadata, CustomCap uses CustomCapTokens, LegacyCustom keeps legacy behavior.",
                allowedValues: EnumNames<ContextPolicyMode>()),
            Field("ContextPolicy.CustomCapTokens", "Context", ConfigFieldType.NullableLong,
                c => c.ContextPolicy.CustomCapTokens, (c, v) => c.ContextPolicy.CustomCapTokens = (long?)v,
                "Custom context window cap in tokens (effective when Mode=CustomCap). Empty clears it.",
                range: (1024, null)),
            Field("ContextPolicy.CompressionThresholdMode", "Context", ConfigFieldType.Enum,
                c => c.ContextPolicy.CompressionThresholdMode,
                (c, v) => c.ContextPolicy.CompressionThresholdMode = Enum.Parse<CompressionThresholdMode>((string)v!, ignoreCase: true),
                "Compression threshold policy: Auto derives from the model window, Custom uses CustomCompressionThresholdTokens.",
                allowedValues: EnumNames<CompressionThresholdMode>()),
            Field("ContextPolicy.CustomCompressionThresholdTokens", "Context", ConfigFieldType.NullableLong,
                c => c.ContextPolicy.CustomCompressionThresholdTokens,
                (c, v) => c.ContextPolicy.CustomCompressionThresholdTokens = (long?)v,
                "Custom compression threshold in tokens (effective when CompressionThresholdMode=Custom). Empty clears it.",
                range: (1, null)),
            Field("ContextPolicy.AutoCompress", "Context", ConfigFieldType.Boolean,
                c => c.ContextPolicy.AutoCompress, (c, v) => c.ContextPolicy.AutoCompress = (bool)v!,
                "Automatically compress conversation history when the threshold is crossed."),
            Field("ContextPolicy.KeepRecentRounds", "Context", ConfigFieldType.Integer,
                c => c.ContextPolicy.KeepRecentRounds, (c, v) => c.ContextPolicy.KeepRecentRounds = (int)v!,
                "Number of recent conversation rounds to keep untouched when compressing.",
                range: (1, 50)),
            Field("ContextPolicy.CompressionStrength", "Context", ConfigFieldType.Enum,
                c => c.ContextPolicy.CompressionStrength,
                (c, v) => c.ContextPolicy.CompressionStrength = Enum.Parse<CompressionStrength>((string)v!, ignoreCase: true),
                "How much history each compression condenses into one summary. "
                + "Conservative=4:1 keeps more detail but compresses more often, Balanced=8:1, "
                + "Aggressive=16:1 absorbs more history per pass with a coarser summary. "
                + "The summary length itself follows from this and does not need to be set.",
                allowedValues: EnumNames<CompressionStrength>()),
            Field("ContextPolicy.TargetSummaryTokens", "Context", ConfigFieldType.Long,
                c => c.ContextPolicy.TargetSummaryTokens, (c, v) => c.ContextPolicy.TargetSummaryTokens = (long)v!,
                "Upper bound on summary length, not a target. The actual length is material divided by "
                + "CompressionStrength; this only caps it further, and is itself capped by what the "
                + "compression model can emit in one response.",
                range: (128, 65_536)),

            // 遗留别名：保持旧工具键可用，并映射到 v6 语义。
            Field("MaxContextTokens", "Context", ConfigFieldType.Long,
                c => (long)c.MaxContextTokens,
                (c, v) =>
                {
                    c.ContextPolicy.Mode = ContextPolicyMode.CustomCap;
                    c.ContextPolicy.CustomCapTokens = (long)v!;
                },
                "Legacy alias for the context window cap. Maps to ContextPolicy.Mode=CustomCap and CustomCapTokens.",
                range: (1024, null)),
            Field("CompressionThreshold", "Context", ConfigFieldType.Long,
                c => (long)c.CompressionThreshold,
                (c, v) =>
                {
                    c.ContextPolicy.CompressionThresholdMode = CompressionThresholdMode.Custom;
                    c.ContextPolicy.CustomCompressionThresholdTokens = (long)v!;
                },
                "Legacy alias for the compression threshold. Maps to ContextPolicy.CompressionThresholdMode=Custom and CustomCompressionThresholdTokens.",
                range: (1, null)),
            Field("AutoCompress", "Context", ConfigFieldType.Boolean,
                c => c.AutoCompress, (c, v) => c.ContextPolicy.AutoCompress = (bool)v!,
                "Legacy alias for ContextPolicy.AutoCompress."),
            Field("KeepRecentRounds", "Context", ConfigFieldType.Integer,
                c => c.KeepRecentRounds, (c, v) => c.ContextPolicy.KeepRecentRounds = (int)v!,
                "Legacy alias for ContextPolicy.KeepRecentRounds.", range: (1, 50)),

            // —— Appearance ——
            Field("Theme", "Appearance", ConfigFieldType.String,
                c => c.Theme, (c, v) => c.Theme = (string)v!,
                "App theme.", allowedValues: ["Dark", "Light"]),
            Field("ColorScheme", "Appearance", ConfigFieldType.String,
                c => c.ColorScheme, (c, v) => c.ColorScheme = (string)v!,
                "Accent color scheme (orthogonal to light/dark theme).",
                allowedValues: ["Default", "Solarized", "Cyberpunk", "Tokyo", "Monokai"]),
            Field("Language", "Appearance", ConfigFieldType.String,
                c => c.Language, (c, v) => c.Language = (string)v!,
                "UI language.", allowedValues: ["en-US", "zh-CN"]),
            Field("FontScale", "Appearance", ConfigFieldType.String,
                c => c.FontScale, (c, v) => c.FontScale = (string)v!,
                "Global font size scale.",
                allowedValues: ["Tiny", "Small", "Medium", "Large", "Maximum"]),

            // —— ChatAudio ——
            Field("ChatAudio.Enabled", "ChatAudio", ConfigFieldType.Boolean,
                c => c.ChatAudioEnabled, (c, v) => c.ChatAudioEnabled = (bool)v!,
                "Enable TTS playback of assistant replies."),
            Field("ChatAudio.Provider", "ChatAudio", ConfigFieldType.String,
                c => c.ChatAudioProvider, (c, v) => c.ChatAudioProvider = (string)v!,
                "TTS provider id.",
                allowedValues: ExtensionProviderCatalog.AudioProviders.Select(p => p.Id).ToArray()),
            Field("ChatAudio.AutoPlay", "ChatAudio", ConfigFieldType.Boolean,
                c => c.ChatAudioAutoPlay, (c, v) => c.ChatAudioAutoPlay = (bool)v!,
                "Auto-play audio after each assistant reply."),

            // —— ImageGeneration ——
            Field("ImageGeneration.Enabled", "ImageGeneration", ConfigFieldType.Boolean,
                c => c.ImageGenerationEnabled, (c, v) => c.ImageGenerationEnabled = (bool)v!,
                "Enable inline image generation."),
            Field("ImageGeneration.Provider", "ImageGeneration", ConfigFieldType.String,
                c => c.ImageGenerationProvider, (c, v) => c.ImageGenerationProvider = (string)v!,
                "Image generation provider id.",
                allowedValues: ExtensionProviderCatalog.ImageProviders.Select(p => p.Id).ToArray()),

            // —— WebSearch ——
            Field("WebSearch.Enabled", "WebSearch", ConfigFieldType.Boolean,
                c => c.WebSearchEnabled, (c, v) => c.WebSearchEnabled = (bool)v!,
                "Enable real-time web search."),
            Field("WebSearch.Provider", "WebSearch", ConfigFieldType.String,
                c => c.WebSearchProvider, (c, v) => c.WebSearchProvider = (string)v!,
                "Web search provider id.",
                allowedValues: ExtensionProviderCatalog.WebSearchProviders.Select(p => p.Id).ToArray()),

            // —— KnowledgeMaintenance ——
            Field("KnowledgeMaintenance.Enabled", "KnowledgeMaintenance", ConfigFieldType.Boolean,
                c => c.KnowledgeMaintenanceEnabled, (c, v) => c.KnowledgeMaintenanceEnabled = (bool)v!,
                "Enable background knowledge base consolidation."),
            Field("KnowledgeMaintenance.IntervalDays", "KnowledgeMaintenance", ConfigFieldType.Integer,
                c => c.KnowledgeMaintenanceIntervalDays, (c, v) => c.KnowledgeMaintenanceIntervalDays = (int)v!,
                "Days between background knowledge maintenance runs.", range: (1, 90)),

            // —— Browser ——
            Field("Browser.Enabled", "Browser", ConfigFieldType.Boolean,
                c => c.BrowserEnabled, (c, v) => c.BrowserEnabled = (bool)v!,
                "Enable the browser automation agent."),
            Field("Browser.Headless", "Browser", ConfigFieldType.Boolean,
                c => c.BrowserHeadless, (c, v) => c.BrowserHeadless = (bool)v!,
                "Run the browser in headless mode."),
            Field("Browser.ObservationMode", "Browser", ConfigFieldType.Enum,
                c => c.BrowserObservationMode,
                (c, v) => c.BrowserObservationMode = Enum.Parse<BrowserObservationMode>((string)v!, ignoreCase: true),
                "Browser agent observation mode.",
                allowedValues: EnumNames<BrowserObservationMode>()),
            Field("Browser.ViewportWidth", "Browser", ConfigFieldType.Integer,
                c => c.BrowserViewportWidth, (c, v) => c.BrowserViewportWidth = (int)v!,
                "Browser viewport width in pixels.", range: (320, 3840)),
            Field("Browser.ViewportHeight", "Browser", ConfigFieldType.Integer,
                c => c.BrowserViewportHeight, (c, v) => c.BrowserViewportHeight = (int)v!,
                "Browser viewport height in pixels.", range: (240, 2160)),
            Field("Browser.MaxSteps", "Browser", ConfigFieldType.Integer,
                c => c.BrowserMaxSteps, (c, v) => c.BrowserMaxSteps = (int)v!,
                "Maximum steps a browser task may take before giving up.", range: (1, 50)),
            Field("Browser.OperationTimeoutSeconds", "Browser", ConfigFieldType.Integer,
                c => c.BrowserOperationTimeoutSeconds, (c, v) => c.BrowserOperationTimeoutSeconds = (int)v!,
                "Per-operation timeout in seconds.", range: (5, 300)),
            Field("Browser.SessionTtlMinutes", "Browser", ConfigFieldType.Integer,
                c => c.BrowserSessionTtlMinutes, (c, v) => c.BrowserSessionTtlMinutes = (int)v!,
                "Browser session time-to-live in minutes.", range: (1, 120)),
            Field("Browser.PersistSession", "Browser", ConfigFieldType.Boolean,
                c => c.BrowserPersistSession, (c, v) => c.BrowserPersistSession = (bool)v!,
                "Persist browser sessions across tasks."),
            Field("Browser.DownloadEnabled", "Browser", ConfigFieldType.Boolean,
                c => c.BrowserDownloadEnabled, (c, v) => c.BrowserDownloadEnabled = (bool)v!,
                "Allow the browser agent to download files."),
            Field("Browser.ScreenshotScale", "Browser", ConfigFieldType.Number,
                c => c.BrowserScreenshotScale, (c, v) => c.BrowserScreenshotScale = (double)v!,
                "Screenshot scale factor for the vision pipeline.", numberRange: (0.25, 2.0)),
            Field("Browser.ImageQuality", "Browser", ConfigFieldType.Integer,
                c => c.BrowserImageQuality, (c, v) => c.BrowserImageQuality = (int)v!,
                "Screenshot JPEG quality (30-100).", range: (30, 100)),
            Field("Browser.SomMaxElements", "Browser", ConfigFieldType.Integer,
                c => c.BrowserSomMaxElements, (c, v) => c.BrowserSomMaxElements = (int)v!,
                "Maximum annotated elements in a Set-of-Marks screenshot.", range: (10, 200)),
            Field("Browser.SomIncludeText", "Browser", ConfigFieldType.Boolean,
                c => c.BrowserSomIncludeText, (c, v) => c.BrowserSomIncludeText = (bool)v!,
                "Include text content in Set-of-Marks annotations."),
            Field("Browser.StructuredOutputMode", "Browser", ConfigFieldType.Enum,
                c => c.BrowserStructuredOutputMode,
                (c, v) => c.BrowserStructuredOutputMode = Enum.Parse<BrowserStructuredOutputMode>((string)v!, ignoreCase: true),
                "Structured output strategy for the browser agent planner.",
                allowedValues: EnumNames<BrowserStructuredOutputMode>()),

            // —— SubAgents ——
            Field("SubAgents.Enabled", "SubAgents", ConfigFieldType.Boolean,
                c => c.EnableSubAgents, (c, v) => c.EnableSubAgents = (bool)v!,
                "Enable parallel sub-agent dispatch (dispatch_subagents)."),
            Field("SubAgents.MaxParallel", "SubAgents", ConfigFieldType.Integer,
                c => c.SubAgentMaxParallel, (c, v) => c.SubAgentMaxParallel = (int)v!,
                "Maximum concurrently running sub-agents (excess is queued).", range: (1, 32)),
            Field("SubAgents.MaxIterations", "SubAgents", ConfigFieldType.Integer,
                c => c.SubAgentMaxIterations, (c, v) => c.SubAgentMaxIterations = (int)v!,
                "Maximum tool-loop iterations per sub-agent.", range: (1, 100)),
            Field("SubAgents.TimeoutSeconds", "SubAgents", ConfigFieldType.Integer,
                c => c.SubAgentTimeoutSeconds, (c, v) => c.SubAgentTimeoutSeconds = (int)v!,
                "Sub-agent run timeout in seconds.", range: (30, 3600)),
            Field("SubAgents.InheritApproval", "SubAgents", ConfigFieldType.Boolean,
                c => c.SubAgentsInheritApproval, (c, v) => c.SubAgentsInheritApproval = (bool)v!,
                "Whether unattended sub-agents inherit the permanent-allow list. Destructive actions stay denied either way."),

            // —— DocumentParser ——
            Field("DocumentParser.Enabled", "DocumentParser", ConfigFieldType.Boolean,
                c => c.DocumentParserEnabled, (c, v) => c.DocumentParserEnabled = (bool)v!,
                "Enable MinerU document parsing for attachments."),
            Field("DocumentParser.Mode", "DocumentParser", ConfigFieldType.Enum,
                c => c.DocumentParserMode,
                (c, v) => c.DocumentParserMode = Enum.Parse<DocumentParserMode>((string)v!, ignoreCase: true),
                "MinerU parsing mode.",
                allowedValues: EnumNames<DocumentParserMode>()),
            Field("DocumentParser.Token", "DocumentParser", ConfigFieldType.String,
                c => c.DocumentParserToken, (c, v) => c.DocumentParserToken = (string)v!,
                "MinerU Precision API token (sensitive; redacted when viewed).", sensitive: true),

            // —— Security ——
            Field("Security.ToolApprovalMode", "Security", ConfigFieldType.Enum,
                c => c.ToolApprovalMode,
                (c, v) => c.ToolApprovalMode = Enum.Parse<ToolApprovalMode>((string)v!, ignoreCase: true),
                "Tool approval gate: Off auto-approves everything, Balanced asks for sensitive/destructive calls, Strict asks for every call.",
                allowedValues: EnumNames<ToolApprovalMode>()),
            Field("Security.AutoAllowedTools", "Security", ConfigFieldType.StringList,
                c => c.AutoAllowedTools.ToList(),
                (c, v) => ReplaceStringList(c.AutoAllowedTools, v),
                "Function names permanently allowed without approval (JSON array or comma-separated)."),
            Field("Security.TerminalAllowlist", "Security", ConfigFieldType.StringList,
                c => c.TerminalAllowlist.ToList(),
                (c, v) => ReplaceStringList(c.TerminalAllowlist, v),
                "Terminal commands allowed without approval (JSON array or comma-separated)."),
            Field("Security.MaxTerminalOutputChars", "Security", ConfigFieldType.Integer,
                c => c.MaxTerminalOutputChars, (c, v) => c.MaxTerminalOutputChars = (int)v!,
                "Max chars per stdout/stderr returned by execute_terminal_command after smart trimming (ANSI cleanup, repeated-block collapse, head/tail retention); longer output is truncated and flagged in the result.",
                range: (1_000, 1_000_000)),

            // —— Extensions ——
            Field("Extensions.EnableMcp", "Extensions", ConfigFieldType.Boolean,
                c => c.EnableMcp, (c, v) => c.EnableMcp = (bool)v!,
                "Enable MCP server connections and meta-tools."),
            Field("Extensions.EnableSkills", "Extensions", ConfigFieldType.Boolean,
                c => c.EnableSkills, (c, v) => c.EnableSkills = (bool)v!,
                "Enable Agent Skills (local workflow instructions)."),

            // —— Runtime（只读）——
            Field("Runtime.ConfigSchemaVersion", "Runtime", ConfigFieldType.Integer,
                c => c.ConfigSchemaVersion, (_, _) => { },
                "Persisted config schema version (read-only).", readOnly: true)
        ]);
        return fields;
    }

    private static IReadOnlyList<ConfigFieldDescriptor> RoleFields()
    {
        var roles = new (string Key, Func<AiModelConfiguration, ModelRoleSettings> Selector)[]
        {
            ("MainConversation", m => m.MainConversation),
            ("TitleGeneration", m => m.TitleGeneration),
            ("ContextCompression", m => m.ContextCompression),
            ("Approval", m => m.Approval),
            ("Embedding", m => m.Embedding),
            ("BrowserAgent", m => m.BrowserAgent),
            ("SubAgent", m => m.SubAgent),
            ("KnowledgeMaintenance", m => m.KnowledgeMaintenance),
            ("ImageRecognition", m => m.ImageRecognition)
        };
        var fields = new List<ConfigFieldDescriptor>(roles.Length * 2);
        foreach (var role in roles)
        {
            fields.Add(Field($"{role.Key}.ProviderId", "AI", ConfigFieldType.String,
                c => role.Selector(c.AiModels).ProviderId,
                (c, v) => role.Selector(c.AiModels).ProviderId = (string)v!,
                "Provider id for this role; use one of the ids listed in summary.providers."));
            fields.Add(Field($"{role.Key}.Model", "AI", ConfigFieldType.String,
                c => role.Selector(c.AiModels).Model,
                (c, v) => role.Selector(c.AiModels).Model = (string)v!,
                "Model id for this role; use an id from the provider's model list."));
        }
        return fields;
    }

    private static ConfigFieldDescriptor Field(
        string key,
        string section,
        ConfigFieldType type,
        Func<AppConfig, object?> get,
        Action<AppConfig, object?> set,
        string description = "",
        IReadOnlyList<string>? allowedValues = null,
        (long Min, long? Max)? range = null,
        (double Min, double? Max)? numberRange = null,
        bool sensitive = false,
        bool readOnly = false) =>
        new()
        {
            Key = key,
            Section = section,
            Type = type,
            Get = get,
            Set = set,
            Description = description,
            AllowedValues = allowedValues,
            Range = range,
            NumberRange = numberRange,
            Sensitive = sensitive,
            ReadOnly = readOnly
        };

    private static string[] EnumNames<TEnum>() where TEnum : struct, Enum =>
        Enum.GetNames<TEnum>();

    private static void ReplaceStringList(ICollection<string> target, object? value)
    {
        target.Clear();
        if (value is IEnumerable<string> items)
        {
            foreach (var item in items) target.Add(item);
        }
    }
}
