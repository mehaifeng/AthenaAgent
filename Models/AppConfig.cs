using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;

namespace Athena.UI.Models;

/// <summary>
/// 应用配置模型
/// </summary>
public partial class AppConfig : ObservableObject
{
    // v4 是三栏、多供应商、多活动会话的全新配置，不迁移旧数据。
    [ObservableProperty]
    private int _configSchemaVersion = 4;

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

    // AI 配置
    [ObservableProperty]
    private string _provider = "OpenAI";

    [ObservableProperty]
    private string _baseUrl = "https://api.openai.com/v1";

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private string _model = "gpt-5-mini";

    [ObservableProperty]
    private double _temperature = 0.7;

    [ObservableProperty]
    private int _maxTokens = 16000;

    [ObservableProperty]
    private double _topP = 1.0;

    [ObservableProperty]
    private int _timeout = 60;

    [ObservableProperty]
    private bool _chatAudioEnabled;

    [ObservableProperty]
    private string _chatAudioProvider = "OpenAI";

    [ObservableProperty]
    private string _chatAudioBaseUrl = "https://api.openai.com/v1/audio/speech";

    [ObservableProperty]
    private string _chatAudioApiKey = string.Empty;

    [ObservableProperty]
    private string _chatAudioModel = "gpt-4o-mini-tts";

    [ObservableProperty]
    private string _chatAudioVoice = "alloy";

    [ObservableProperty]
    private bool _chatAudioAutoPlay;

    [ObservableProperty]
    private string _chatAudioLanguage = "en-US";

    [ObservableProperty]
    private double _chatAudioSpeed = 1.0;

    // Edge/KittenTTS/Piper use a locally installed executable or Python runtime.
    [ObservableProperty]
    private string _chatAudioLocalExecutable = string.Empty;

    [ObservableProperty]
    private string _chatAudioLocalModelPath = string.Empty;

    [ObservableProperty]
    private bool _imageGenerationEnabled = true;

    [ObservableProperty]
    private string _imageGenerationModel = "gpt-image-1";

    [ObservableProperty]
    private string _imageGenerationBaseUrl = string.Empty;

    [ObservableProperty]
    private string _imageGenerationApiKey = string.Empty;

    [ObservableProperty]
    private string _imageGenerationProvider = "OpenAI";

    [ObservableProperty]
    private string _imageGenerationAspectRatio = "1:1";

    // Embedding 模型配置（用于向量检索）
    [ObservableProperty]
    private EmbeddingConnectionSource _embeddingCredentialSource = EmbeddingConnectionSource.Provider;

    [ObservableProperty]
    private string _embeddingProvider = "OpenAI";

    [ObservableProperty]
    private string _embeddingBaseUrl = string.Empty;

    [ObservableProperty]
    private string _embeddingApiKey = string.Empty;

    [ObservableProperty]
    private string _embeddingModel = "text-embedding-3-small";

    // 知识库定期整理（后台合并去重）
    [ObservableProperty]
    private bool _knowledgeMaintenanceEnabled = true;

    [ObservableProperty]
    private int _knowledgeMaintenanceIntervalDays = 7;

    // 整理 Agent 模型来源：默认跟随次级（后台任务）模型；Custom 时用下方独立字段（留空逐字段回退次级→主）。
    [ObservableProperty]
    private KnowledgeMaintenanceModelSource _knowledgeMaintenanceModelSource = KnowledgeMaintenanceModelSource.InheritSecondary;

    [ObservableProperty]
    private string _knowledgeMaintenanceProvider = "OpenAI";

    [ObservableProperty]
    private string _knowledgeMaintenanceBaseUrl = string.Empty;

    [ObservableProperty]
    private string _knowledgeMaintenanceApiKey = string.Empty;

    [ObservableProperty]
    private string _knowledgeMaintenanceModel = string.Empty;

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

    [ObservableProperty]
    private string _webSearchBaseUrl = "https://api.tavily.com";

    [ObservableProperty]
    private string _webSearchApiKey = string.Empty;

    [ObservableProperty]
    private string _webSearchAppId = string.Empty;

    [ObservableProperty]
    private string _webSearchModel = "grok-4-fast";

    [ObservableProperty]
    private string _webSearchMode = "fast";

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

    // 浏览器智能体模型：独立凭据，不继承主对话模型。
    [ObservableProperty]
    private string _browserAgentProvider = "OpenAI";

    [ObservableProperty]
    private string _browserAgentBaseUrl = string.Empty;

    [ObservableProperty]
    private string _browserAgentApiKey = string.Empty;

    [ObservableProperty]
    private string _browserAgentModel = "gpt-4o-mini";

    [ObservableProperty]
    private int _browserAgentMaxTokens = 16000;

    [ObservableProperty]
    private double _browserAgentTemperature = 0.2;

    // 浏览器 Agent 结构化输出策略：Auto 乐观启用 json_object 并在后端拒绝时自动降级。
    [ObservableProperty]
    private BrowserStructuredOutputMode _browserStructuredOutputMode = BrowserStructuredOutputMode.Auto;

    // 子代理配置（dispatch_subagents：主模型并行派生隔离上下文的子代理）
    [ObservableProperty]
    private bool _enableSubAgents = false;

    // 子代理模型来源：默认跟随主模型；Custom 时使用下方独立字段（留空逐字段回退主 AI）。
    [ObservableProperty]
    private SubAgentModelSource _subAgentModelSource = SubAgentModelSource.InheritMain;

    [ObservableProperty]
    private string _subAgentProvider = "OpenAI";

    [ObservableProperty]
    private string _subAgentBaseUrl = string.Empty;

    [ObservableProperty]
    private string _subAgentApiKey = string.Empty;

    [ObservableProperty]
    private string _subAgentModel = "gpt-4o-mini";

    [ObservableProperty]
    private int _subAgentMaxTokens = 16000;

    [ObservableProperty]
    private double _subAgentTemperature = 0.3;

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

    [ObservableProperty]
    private string _chatAudioProviderId = string.Empty;

    [ObservableProperty]
    private string _chatAudioEndpointPath = "/audio/speech";

    [ObservableProperty]
    private string _imageGenerationProviderId = string.Empty;

    [ObservableProperty]
    private string _imageGenerationEndpointPath = "/images/generations";

    [ObservableProperty]
    private string _browserProviderId = string.Empty;

    [ObservableProperty]
    private string _browserEndpointPath = string.Empty;
}

public partial class MainLayoutSettings : ObservableObject
{
    [ObservableProperty]
    private double _leftWidth = 292;

    [ObservableProperty]
    private double _rightWidth = 470;

    [ObservableProperty]
    private double _rightTopHeight = 560;

    [ObservableProperty]
    private double _editorWidth = 280;

    [ObservableProperty]
    private bool _sidePanelsSwapped;
}
