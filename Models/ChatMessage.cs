using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace Athena.UI.Models;

/// <summary>
/// 聊天消息模型
/// </summary>
public partial class ChatMessage : ObservableObject
{
    /// <summary>
    /// 消息角色: user, assistant, system
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    [NotifyPropertyChangedFor(nameof(IsUser))]
    [NotifyPropertyChangedFor(nameof(IsVisibleToUser))]
    private string _role = string.Empty;

    /// <summary>
    /// 消息内容
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private string _content = string.Empty;

    /// <summary>
    /// 编辑中的临时内容
    /// </summary>
    [ObservableProperty]
    private string _editContent = string.Empty;

    /// <summary>
    /// 时间戳
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimestampText))]
    private DateTime _timestamp = DateTime.Now;

    /// <summary>
    /// 是否为心跳消息（AI 主动发起）
    /// </summary>
    [ObservableProperty]
    private bool _isHeartbeat;

    /// <summary>
    /// 是否正在加载中
    /// </summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// 是否正在编辑中
    /// </summary>
    [ObservableProperty]
    private bool _isEditing;

    /// <summary>
    /// 显示文本（纯内容，不带前缀）
    /// </summary>
    public string DisplayText => Content;

    /// <summary>
    /// 时间戳显示格式
    /// </summary>
    public string TimestampText => Timestamp.ToString("[HH:mm:ss]");

    /// <summary>
    /// 角色显示图标
    /// </summary>
    public string RoleIcon => Role switch
    {
        "user" => ">",
        "assistant" => "<",
        "system" => "*",
        "error" => "!",
        _ => "-"
    };

    /// <summary>
    /// 是否为用户消息
    /// </summary>
    public bool IsUser => Role == "user";

    /// <summary>
    /// 是否在 UI 中可见（system 消息只对 LLM 可见，不对用户显示）
    /// </summary>
    public bool IsVisibleToUser => Role != "system";
}
