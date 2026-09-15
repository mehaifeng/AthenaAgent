using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
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

    /// <summary>
    /// 供应商 <c>/v1/models</c> 自报的元数据；端点没报过就是 null。
    /// 随库存一起持久化，这样启动后不必先联网就有窗口大小可用。
    /// </summary>
    public ProviderReportedModelMetadata? Reported { get; set; }
}

/// <summary>
/// 供应商自己在 <c>/v1/models</c> 里给出的能力元数据。
///
/// OpenAI 协议只要求 id/object/created/owned_by，但聚合型端点普遍在这之外多报几个字段：
/// OrcaRouter 实测 196 个模型里 153 个带 <c>context_length</c>、162 个带 <c>architecture</c>，
/// 并用 <c>supported_endpoint_types</c> 区分 <c>openai</c> 与 <c>openai-response</c>。
/// 这些值来自实际承接请求的那一家，比拿模型 ID 去模糊匹配 OpenRouter 目录可靠，
/// 所以在 <see cref="MetadataValueSource"/> 里排在 OpenRouter 两层之上。
///
/// 每个字段都可空，"没报"与"报了 0"必须能区分——把缺失折成 0 会让解析层
/// 误以为拿到了一个真实的上下文窗口。
/// </summary>
public sealed class ProviderReportedModelMetadata
{
    public long? ContextLength { get; set; }

    public long? MaxCompletionTokens { get; set; }

    public List<string>? InputModalities { get; set; }

    public List<string>? OutputModalities { get; set; }

    /// <summary>该模型可用的协议门面，例如 <c>openai</c> / <c>openai-response</c> / <c>anthropic</c> / <c>gemini</c>。</summary>
    public List<string>? SupportedEndpointTypes { get; set; }

    public bool HasAnyValue => ContextLength.HasValue
        || MaxCompletionTokens.HasValue
        || InputModalities is { Count: > 0 }
        || OutputModalities is { Count: > 0 }
        || SupportedEndpointTypes is { Count: > 0 };
}

public enum ModelMetadataBindingMode
{
    Automatic,
    PinnedOpenRouter,
    CustomOnly
}

/// <summary>模型推理强度（reasoning.effort）。Auto 表示不显式设置、由端点默认。</summary>
public enum ReasoningEffort
{
    Auto,
    None,
    Minimal,
    Low,
    Medium,
    High,
    XHigh,
    Max
}

public partial class ModelMetadataOverrides : ObservableObject
{
    [ObservableProperty] private long? _contextWindowTokens;
    [ObservableProperty] private long? _maxCompletionTokens;
    [ObservableProperty] private bool? _supportsTools;
    [ObservableProperty] private bool? _supportsReasoning;
    [ObservableProperty] private bool? _supportsStructuredOutput;
    [ObservableProperty] private bool? _supportsResponses;
    [ObservableProperty] private ReasoningEffort _reasoningEffort = ReasoningEffort.Auto;
    [ObservableProperty] private ObservableCollection<string>? _inputModalities;
    [ObservableProperty] private ObservableCollection<string>? _outputModalities;

    public bool HasAnyValue => ContextWindowTokens.HasValue
        || MaxCompletionTokens.HasValue
        || SupportsTools.HasValue
        || SupportsReasoning.HasValue
        || SupportsStructuredOutput.HasValue
        || SupportsResponses.HasValue
        || ReasoningEffort != ReasoningEffort.Auto
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

    /// <summary>
    /// 虚拟宠物台词。留空时借用标题生成角色——两者都是"一句话、低延迟、便宜"的场景，
    /// 没必要为了玩具功能逼用户多配一个模型。
    /// </summary>
    [ObservableProperty]
    private ModelRoleSettings _companion = new();
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
    ImageRecognition,
    Companion
}
