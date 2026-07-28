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
