using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace Athena.UI.Services;

/// <summary>
/// 跨页面共享的 Token 统计服务
/// </summary>
public interface ITokenService
{
    int CurrentTokens { get; set; }
    int MaxTokens { get; set; }
    string CompressionPreview { get; set; }
    string TokenInfoText { get; }
    bool IsWarningLimit { get; }
    bool IsNearLimit { get; }
}

public partial class TokenService : ObservableObject, ITokenService
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenInfoText))]
    [NotifyPropertyChangedFor(nameof(IsWarningLimit))]
    [NotifyPropertyChangedFor(nameof(IsNearLimit))]
    private int _currentTokens;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenInfoText))]
    [NotifyPropertyChangedFor(nameof(IsWarningLimit))]
    [NotifyPropertyChangedFor(nameof(IsNearLimit))]
    private int _maxTokens = 4000;

    [ObservableProperty]
    private string _compressionPreview = string.Empty;

    public string TokenInfoText => $"{CurrentTokens} / {MaxTokens} tokens";

    public bool IsWarningLimit => MaxTokens > 0 && CurrentTokens >= MaxTokens * 0.6 && CurrentTokens < MaxTokens * 0.8;

    public bool IsNearLimit => MaxTokens > 0 && CurrentTokens >= MaxTokens * 0.8;
}
