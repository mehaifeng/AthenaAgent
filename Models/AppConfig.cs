using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace Athena.UI.Models;

/// <summary>
/// 应用配置模型
/// </summary>
public partial class AppConfig : ObservableObject
{
    // v5 removes legacy provider compatibility fields and intentionally does not migrate older schemas.
    [ObservableProperty]
    private int _configSchemaVersion = 5;

    [ObservableProperty]
    private AiModelConfiguration _aiModels = new();

    [ObservableProperty]
    private int _mainConversationMaxParallel = 4;

    [ObservableProperty]
    private MainLayoutSettings _mainLayout = new();

    // 外观设置
    [ObservableProperty]
    private string _theme = "Dark";

    [ObservableProperty]
    private string _language = "zh-CN";

    [ObservableProperty]
    private double _topP = 1.0;

    [ObservableProperty]
    private int _timeout = 60;

    [ObservableProperty]
    private bool _chatAudioEnabled;

    [ObservableProperty]
    private string _chatAudioProvider = "OpenAI";

    [ObservableProperty]
    private bool _chatAudioAutoPlay;

    [ObservableProperty]
    private bool _imageGenerationEnabled = true;

    [ObservableProperty]
    private string _imageGenerationProvider = "OpenAI";

    // 知识库定期整理（后台合并去重）
    [ObservableProperty]
    private bool _knowledgeMaintenanceEnabled = true;

    [ObservableProperty]
    private int _knowledgeMaintenanceIntervalDays = 7;

    // 记忆配置
    [ObservableProperty]
    private int _maxContextTokens = 128000;

    [ObservableProperty]
    private int _compressionThreshold = 64000;

    [ObservableProperty]
    private bool _autoCompress = true;

    [ObservableProperty]
    private int _keepRecentRounds = 3;

    // 文件系统控制策略
    [ObservableProperty]
    private FileSystemPolicyConfig _fileSystemPolicy = new();

    // Web Search 配置
    [ObservableProperty]
    private bool _webSearchEnabled = false;

    [ObservableProperty]
    private string _webSearchProvider = "Tavily";

    // Each extension provider keeps its own credentials and protocol-specific options.
    [ObservableProperty]
    private ObservableCollection<ExtensionProviderSettings> _webSearchProviderSettings = [];

    [ObservableProperty]
    private ObservableCollection<ExtensionProviderSettings> _imageProviderSettings = [];

    [ObservableProperty]
    private ObservableCollection<ExtensionProviderSettings> _audioProviderSettings = [];

    // 自动化浏览器采用内置的安全默认参数；常规 UI 不暴露这些实现细节。
    [ObservableProperty]
    private bool _browserEnabled = true;

    [ObservableProperty]
    private bool _browserHeadless = true;

    [ObservableProperty]
    private BrowserObservationMode _browserObservationMode = BrowserObservationMode.VisionWithSom;

    [ObservableProperty]
    private int _browserViewportWidth = 1280;

    [ObservableProperty]
    private int _browserViewportHeight = 900;

    [ObservableProperty]
    private int _browserMaxSteps = 12;

    [ObservableProperty]
    private int _browserOperationTimeoutSeconds = 30;

    [ObservableProperty]
    private int _browserSessionTtlMinutes = 10;

    [ObservableProperty]
    private bool _browserPersistSession = false;

    [ObservableProperty]
    private bool _browserDownloadEnabled = false;

    [ObservableProperty]
    private double _browserScreenshotScale = 1.0;

    [ObservableProperty]
    private int _browserImageQuality = 85;

    [ObservableProperty]
    private int _browserSomMaxElements = 80;

    [ObservableProperty]
    private bool _browserSomIncludeText = true;

    // 浏览器 Agent 结构化输出策略：Auto 乐观启用 json_object 并在后端拒绝时自动降级。
    [ObservableProperty]
    private BrowserStructuredOutputMode _browserStructuredOutputMode = BrowserStructuredOutputMode.Auto;

    // 子代理配置（dispatch_subagents：主模型并行派生隔离上下文的子代理）
    [ObservableProperty]
    private bool _enableSubAgents = false;

    // 同时并行运行的子代理上限（超出排队）。
    [ObservableProperty]
    private int _subAgentMaxParallel = 4;

    // 单个子代理的工具循环最大轮数（兜底）。
    [ObservableProperty]
    private int _subAgentMaxIterations = 20;

    [ObservableProperty]
    private int _subAgentTimeoutSeconds = 180;

    // 文档解析配置（MinerU）
    [ObservableProperty]
    private bool _documentParserEnabled = false;

    [ObservableProperty]
    private DocumentParserMode _documentParserMode = DocumentParserMode.AgentLightweight;

    [ObservableProperty]
    private string _documentParserToken = string.Empty;

    // 工具审批（Tool-Use Approval）
    // 均衡模式：只读放行，写/删/终端等敏感与破坏性操作执行前弹窗确认。默认开箱即安全。
    [ObservableProperty]
    private ToolApprovalMode _toolApprovalMode = ToolApprovalMode.Balanced;

    // 用户勾选「永久允许」后记录的工具函数名（跳过后续审批）。
    [ObservableProperty]
    private ObservableCollection<string> _autoAllowedTools = new();

    // 用户信任、可自动放行的终端命令名（如 git、node）。
    [ObservableProperty]
    private ObservableCollection<string> _terminalAllowlist = new();

    // 子代理等无人值守路径是否沿用永久放行清单（true）。破坏性操作无论如何都拒绝。
    [ObservableProperty]
    private bool _subAgentsInheritApproval = false;

    // MCP 扩展（Model Context Protocol）—— 外部工具服务器接入
    // EnableMcp 关闭时，FunctionRegistry 隐藏三个 meta-tool，配置项亦不参与生命周期。
    [ObservableProperty]
    private bool _enableMcp = false;

    [ObservableProperty]
    private ObservableCollection<McpServerConfig> _mcpServers = new();

    // Agent Skills — local, progressively disclosed workflow instructions.
    [ObservableProperty]
    private bool _enableSkills = false;

    // Source scope + canonical directory. This avoids disabling a same-named Skill from another scope.
    [ObservableProperty]
    private ObservableCollection<string> _disabledSkillKeys = new();

    // 用户偏好设置
    [ObservableProperty]
    private bool _skipRewindConfirm;

    // 首次启动引导是否已完成（完成或跳过均置 true，之后不再弹出）。
    [ObservableProperty]
    private bool _onboardingCompleted;

    // 工作区配置
    // 最近活跃的工作区 ID（启动时恢复，null 表示无活跃工作区）。
    [ObservableProperty]
    private string? _lastActiveWorkspaceId;

    // 工作区知识文件全量注入 system prompt 时的 token 预算上限。
    [ObservableProperty]
    private int _workspaceKnowledgeTokenBudget = 2000;

}

public partial class MainLayoutSettings : ObservableObject
{
    [ObservableProperty]
    private double _leftWidth = 292;

    [ObservableProperty]
    private double _rightWidth = 470;

    [ObservableProperty]
    private double _rightLogHeight = 190;

    [ObservableProperty]
    private double _reviewWidth = 280;

    [ObservableProperty]
    private double _editorWidth = 280;

    [ObservableProperty]
    private double _fileTreeWidth = 180;

    [ObservableProperty]
    private bool _sidePanelsSwapped;

    // 三块 shell-panel 的背景透明度：0 = 完全不透明（图像被压住，与原观感一致），
    // 0.5 = 50% 透明（雅典娜图像从面板后淡淡透出）。标题栏与设置窗口不受影响。
    // 这里存的是"透明度"分率（0–0.5），VM 会转成 `Opacity = 1 - PanelTransparency` 给 XAML。
    [ObservableProperty]
    private double _panelTransparency;

    partial void OnPanelTransparencyChanged(double value)
    {
        // 越界值直接夹回区间 [0, 0.5]，避免外部写入导致 Shell-panel 完全消失或过度穿透。
        if (value < 0.0)
            PanelTransparency = 0.0;
        else if (value > 0.5)
            PanelTransparency = 0.5;
    }
}
