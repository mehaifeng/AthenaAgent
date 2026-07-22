using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;

namespace Athena.UI.Models;

/// <summary>
/// 一个可复用的 OpenAI SDK 兼容连接。业务模型只引用此配置，不重复保存 BaseUrl/API Key。
/// </summary>
public partial class OpenAiProviderConfiguration : ObservableObject
{
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private string _displayName = "OpenAI Official";

    [ObservableProperty]
    private string _providerPreset = "OpenAI";

    [ObservableProperty]
    private string _baseUrl = "https://api.openai.com/v1";

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private ObservableCollection<ProviderModelDescriptor> _models = new();

    [ObservableProperty]
    private DateTimeOffset? _modelsRefreshedAt;
}

public sealed class ProviderModelDescriptor
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public ModelCapability Capability { get; set; } = ModelCapability.Unknown;

    public bool IsManual { get; set; }
}

public enum ModelCapability
{
    Unknown,
    Text,
    Embedding,
    Image,
    Speech
}

/// <summary>某个业务角色使用的模型，以及该角色自己的采样/输出参数。</summary>
public partial class ModelRoleSettings : ObservableObject
{
    [ObservableProperty]
    private string _providerId = string.Empty;

    [ObservableProperty]
    private string _model = string.Empty;

    [ObservableProperty]
    private double _temperature = 0.3;

    [ObservableProperty]
    private int _maxOutputTokens = 16000;
}

/// <summary>
/// 所有 OpenAI SDK 兼容业务模型的明确分工。TTS 与生图使用 Extensions 中的独立连接，不在此继承。
/// </summary>
public partial class AiModelConfiguration : ObservableObject
{
    /// <summary>新配置的唯一供应商集合。角色只保存稳定 ProviderId 与 Model。</summary>
    [ObservableProperty]
    private ObservableCollection<OpenAiProviderConfiguration> _providers = new();

    // 仅保留给旧页面的过渡绑定；新运行时和新窗口不再读取它。
    [ObservableProperty]
    private OpenAiProviderConfiguration _provider = new();

    [ObservableProperty]
    private ModelRoleSettings _mainConversation = new()
    {
        Temperature = 0.7
    };

    [ObservableProperty]
    private ModelRoleSettings _titleGeneration = new()
    {
        Temperature = 0.2
    };

    [ObservableProperty]
    private ModelRoleSettings _contextCompression = new()
    {
        Temperature = 0.2
    };

    [ObservableProperty]
    private ModelRoleSettings _approval = new()
    {
        Temperature = 0,
        MaxOutputTokens = 256
    };

    [ObservableProperty]
    private ModelRoleSettings _embedding = new()
    {
        Temperature = 0,
        MaxOutputTokens = 0
    };

    [ObservableProperty]
    private ModelRoleSettings _browserAgent = new()
    {
        Temperature = 0.2
    };

    [ObservableProperty]
    private ModelRoleSettings _subAgent = new()
    {
        Temperature = 0.3
    };

    [ObservableProperty]
    private ModelRoleSettings _knowledgeMaintenance = new()
    {
        Temperature = 0.1,
        MaxOutputTokens = 4096
    };

    [ObservableProperty]
    private ModelRoleSettings _imageRecognition = new()
    {
        Temperature = 0.1,
        MaxOutputTokens = 4096
    };
}

public enum AiModelRole
{
    MainConversation,
    TitleGeneration,
    ContextCompression,
    Approval,
    Embedding,
    BrowserAgent,
    SubAgent,
    KnowledgeMaintenance,
    ImageRecognition
}
