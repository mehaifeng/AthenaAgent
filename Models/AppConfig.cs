using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;

namespace Athena.UI.Models;

/// <summary>
/// 应用配置模型
/// </summary>
public partial class AppConfig : ObservableObject
{
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
    private int _maxTokens = 8000;

    [ObservableProperty]
    private double _topP = 1.0;

    [ObservableProperty]
    private int _timeout = 60;

    [ObservableProperty]
    private bool _enableFunctionCalling = true;

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
    private bool _imageGenerationEnabled = true;

    [ObservableProperty]
    private string _imageGenerationModel = "gpt-image-1";

    [ObservableProperty]
    private string _imageGenerationBaseUrl = string.Empty;

    [ObservableProperty]
    private string _imageGenerationApiKey = string.Empty;

    // 次级模型配置（用于摘要生成等后台任务）
    [ObservableProperty]
    private string _secondaryProvider = "OpenAI";

    [ObservableProperty]
    private string _secondaryBaseUrl = string.Empty;

    [ObservableProperty]
    private string _secondaryApiKey = string.Empty;

    [ObservableProperty]
    private string _secondaryModel = "gpt-4o-mini";

    [ObservableProperty]
    private double _secondaryTemperature = 0.3;

    [ObservableProperty]
    private int _secondaryMaxTokens = 500;

    // Embedding 模型配置（用于向量检索）
    [ObservableProperty]
    private string _embeddingProvider = "OpenAI";

    [ObservableProperty]
    private string _embeddingBaseUrl = string.Empty;

    [ObservableProperty]
    private string _embeddingApiKey = string.Empty;

    [ObservableProperty]
    private string _embeddingModel = "text-embedding-3-small";

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

    // Headless Browser 配置
    [ObservableProperty]
    private bool _browserEnabled = false;

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

    // 浏览器智能体模型来源：默认跟随主模型，避免主模型已支持视觉时还要重复配置。
    // 旧配置在 ConfigService 加载时会被迁移为 Custom 以保持原有行为。
    [ObservableProperty]
    private BrowserModelSource _browserModelSource = BrowserModelSource.InheritMain;

    [ObservableProperty]
    private string _browserAgentProvider = "OpenAI";

    [ObservableProperty]
    private string _browserAgentBaseUrl = string.Empty;

    [ObservableProperty]
    private string _browserAgentApiKey = string.Empty;

    [ObservableProperty]
    private string _browserAgentModel = "gpt-4o-mini";

    [ObservableProperty]
    private int _browserAgentMaxTokens = 1000;

    [ObservableProperty]
    private double _browserAgentTemperature = 0.2;

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
    private int _subAgentMaxTokens = 4000;

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

    // 用户偏好设置
    [ObservableProperty]
    private bool _skipRewindConfirm;
}
