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
    private int _fontSize = 14;

    [ObservableProperty]
    private bool _showHeartbeatButton = true;

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
    private string _model = "gpt-4-turbo";

    [ObservableProperty]
    private double _temperature = 0.7;

    [ObservableProperty]
    private int _maxTokens = 2000;

    [ObservableProperty]
    private double _topP = 1.0;

    [ObservableProperty]
    private int _timeout = 60;

    [ObservableProperty]
    private bool _enableFunctionCalling = true;

    // 次级模型配置（用于摘要生成等后台任务）
    [ObservableProperty]
    private string _secondaryProvider = "OpenAI";

    [ObservableProperty]
    private string _secondaryBaseUrl = "https://api.openai.com/v1";

    [ObservableProperty]
    private string _secondaryApiKey = string.Empty;

    [ObservableProperty]
    private string _secondaryModel = "gpt-3.5-turbo";

    [ObservableProperty]
    private double _secondaryTemperature = 0.3;

    [ObservableProperty]
    private int _secondaryMaxTokens = 500;

    // Embedding 模型配置（用于向量检索）
    [ObservableProperty]
    private string _embeddingProvider = "OpenAI";

    [ObservableProperty]
    private string _embeddingBaseUrl = "https://api.openai.com/v1";

    [ObservableProperty]
    private string _embeddingApiKey = string.Empty;

    [ObservableProperty]
    private string _embeddingModel = "text-embedding-3-small";

    // 记忆配置
    [ObservableProperty]
    private int _maxContextTokens = 8000;

    [ObservableProperty]
    private int _compressionThreshold = 6000;

    [ObservableProperty]
    private bool _autoCompress = true;
}

/// <summary>
/// 计划任务模型
/// </summary>
public partial class ScheduledTask : ObservableObject
{
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString();

    [ObservableProperty]
    private DateTime _triggerTime;

    [ObservableProperty]
    private string _intent = string.Empty;

    [ObservableProperty]
    private string _recurrence = "none";

    [ObservableProperty]
    private bool _isExecuted;

    [ObservableProperty]
    private DateTime _createdAt = DateTime.Now;

    public string TriggerTimeDisplay => TriggerTime.ToString("yyyy-MM-dd HH:mm");

    public string RecurrenceDisplay => Recurrence switch
    {
        "none" => "一次性",
        "daily" => "每天",
        "weekly" => "每周",
        _ => Recurrence
    };
}

/// <summary>
/// 知识库文件节点
/// </summary>
public partial class KnowledgeFileNode : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _isDirectory;

    [ObservableProperty]
    private string _fullPath = string.Empty;

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>
    /// 子节点集合（用于目录）
    /// </summary>
    public ObservableCollection<KnowledgeFileNode> Children { get; } = new();
}
