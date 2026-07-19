using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace Athena.UI.Models;

/// <summary>
/// 一个可复用的 OpenAI SDK 兼容连接。业务模型只引用此配置，不重复保存 BaseUrl/API Key。
/// </summary>
public partial class OpenAiProviderConfiguration : ObservableObject
{
    [ObservableProperty]
    private string _displayName = "OpenAI Official";

    [ObservableProperty]
    private string _providerPreset = "OpenAI";

    [ObservableProperty]
    private string _baseUrl = "https://api.openai.com/v1";

    [ObservableProperty]
    private string _apiKey = string.Empty;
}

/// <summary>某个业务角色使用的模型，以及该角色自己的采样/输出参数。</summary>
public partial class ModelRoleSettings : ObservableObject
{
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
    [ObservableProperty]
    private OpenAiProviderConfiguration _provider = new();

    [ObservableProperty]
    private ModelRoleSettings _mainConversation = new()
    {
        Model = "gpt-5-mini",
        Temperature = 0.7
    };

    [ObservableProperty]
    private ModelRoleSettings _titleGeneration = new()
    {
        Model = "gpt-4o-mini",
        Temperature = 0.2
    };

    [ObservableProperty]
    private ModelRoleSettings _contextCompression = new()
    {
        Model = "gpt-4o-mini",
        Temperature = 0.2
    };

    [ObservableProperty]
    private ModelRoleSettings _approval = new()
    {
        Model = "gpt-4o-mini",
        Temperature = 0,
        MaxOutputTokens = 256
    };

    [ObservableProperty]
    private ModelRoleSettings _embedding = new()
    {
        Model = "text-embedding-3-small",
        Temperature = 0,
        MaxOutputTokens = 0
    };

    [ObservableProperty]
    private ModelRoleSettings _browserAgent = new()
    {
        Model = "gpt-4o-mini",
        Temperature = 0.2
    };

    [ObservableProperty]
    private ModelRoleSettings _subAgent = new()
    {
        Model = "gpt-4o-mini",
        Temperature = 0.3
    };

    [ObservableProperty]
    private ModelRoleSettings _knowledgeMaintenance = new()
    {
        Model = "gpt-4o-mini",
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
    KnowledgeMaintenance
}
