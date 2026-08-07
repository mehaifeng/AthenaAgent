using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;

namespace Athena.UI.Models;

/// <summary>传输协议：Chat Completions（默认）/ Responses / 自动判定。</summary>
public enum ProviderProtocol
{
    Auto,
    ChatCompletions,
    Responses
}

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

    /// <summary>请求传输协议。Auto 时按端点身份与模型元数据保守判定（见 ResponsesProtocolResolver）。</summary>
    [ObservableProperty]
    private ProviderProtocol _protocol = ProviderProtocol.Auto;

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

    /// <summary>最近一次供应商库存中是否存在；被引用但暂时消失的模型保留为 false。</summary>
    public bool IsAvailable { get; set; } = true;
}

public enum ModelMetadataBindingMode
{
    Automatic,
    PinnedOpenRouter,
    CustomOnly
}

public partial class ModelMetadataOverrides : ObservableObject
{
    [ObservableProperty] private long? _contextWindowTokens;
    [ObservableProperty] private long? _maxCompletionTokens;
    [ObservableProperty] private bool? _supportsTools;
    [ObservableProperty] private bool? _supportsReasoning;
    [ObservableProperty] private bool? _supportsStructuredOutput;
    [ObservableProperty] private bool? _supportsResponses;
    [ObservableProperty] private ObservableCollection<string>? _inputModalities;
    [ObservableProperty] private ObservableCollection<string>? _outputModalities;

    public bool HasAnyValue => ContextWindowTokens.HasValue
        || MaxCompletionTokens.HasValue
        || SupportsTools.HasValue
        || SupportsReasoning.HasValue
        || SupportsStructuredOutput.HasValue
        || SupportsResponses.HasValue
        || InputModalities is { Count: > 0 }
        || OutputModalities is { Count: > 0 };
}

/// <summary>仅保存用户意图；自动匹配结果不写入配置。</summary>
public partial class ProviderModelMetadataProfile : ObservableObject
{
    [ObservableProperty] private string _providerId = string.Empty;
    [ObservableProperty] private string _externalModelId = string.Empty;
    [ObservableProperty] private ModelMetadataBindingMode _bindingMode = ModelMetadataBindingMode.Automatic;
    [ObservableProperty] private string? _pinnedOpenRouterModelId;
    [ObservableProperty] private ModelMetadataOverrides _overrides = new();
}

public enum ModelCapability
{
    Unknown,
    Text,
    Embedding,
    Image,
    Speech
}

/// <summary>某个业务角色使用的 Provider 和模型。</summary>
public partial class ModelRoleSettings : ObservableObject
{
    [ObservableProperty]
    private string _providerId = string.Empty;

    [ObservableProperty]
    private string _model = string.Empty;
}

/// <summary>
/// 所有 OpenAI SDK 兼容业务模型的明确分工。TTS 与生图使用 Extensions 中的独立连接，不在此继承。
/// </summary>
public partial class AiModelConfiguration : ObservableObject
{
    /// <summary>新配置的唯一供应商集合。角色只保存稳定 ProviderId 与 Model。</summary>
    [ObservableProperty]
    private ObservableCollection<OpenAiProviderConfiguration> _providers = new();

    [ObservableProperty]
    private ObservableCollection<ProviderModelMetadataProfile> _modelMetadataProfiles = new();

    [ObservableProperty]
    private ModelRoleSettings _mainConversation = new();

    [ObservableProperty]
    private ModelRoleSettings _titleGeneration = new();

    [ObservableProperty]
    private ModelRoleSettings _contextCompression = new();

    [ObservableProperty]
    private ModelRoleSettings _approval = new();

    [ObservableProperty]
    private ModelRoleSettings _embedding = new();

    [ObservableProperty]
    private ModelRoleSettings _browserAgent = new();

    [ObservableProperty]
    private ModelRoleSettings _subAgent = new();

    [ObservableProperty]
    private ModelRoleSettings _knowledgeMaintenance = new();

    [ObservableProperty]
    private ModelRoleSettings _imageRecognition = new();
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
