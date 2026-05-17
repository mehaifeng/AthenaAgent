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
    private string _chatAudioVoice = "alloy";

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

    [ObservableProperty]
    private string _browserVisionProvider = "OpenAI";

    [ObservableProperty]
    private string _browserVisionBaseUrl = string.Empty;

    [ObservableProperty]
    private string _browserVisionApiKey = string.Empty;

    [ObservableProperty]
    private string _browserVisionModel = "gpt-4o-mini";

    [ObservableProperty]
    private int _browserVisionMaxTokens = 1000;

    [ObservableProperty]
    private double _browserVisionTemperature = 0.2;

    // 用户偏好设置
    [ObservableProperty]
    private bool _skipRegenerateConfirm;

    [ObservableProperty]
    private bool _skipEditConfirm;
}
