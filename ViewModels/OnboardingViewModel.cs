using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;

namespace Athena.UI.ViewModels;

/// <summary>
/// 首次启动引导向导（3 步：语言+主凭据 → 能力开关 → 起手语）。
/// 依赖 Feature A 的统一凭据继承树：Step 1 只填一份主凭据即可点亮全部模型角色。
/// 关窗即视为跳过（App.axaml.cs 的 Closed 处理是安全网），向导永不阻塞用户。
/// </summary>
public partial class OnboardingViewModel : ObservableObject
{
    private readonly IConfigService _configService;
    private readonly ILocalizationService? _localizationService;
    private readonly IChatService? _chatService;
    private readonly IModelCatalogService? _modelCatalogService;
    private CancellationTokenSource? _modelOptionsCts;

    /// <summary>请求关闭窗口（由 OnboardingWindow 注入）。</summary>
    public Action? RequestClose { get; set; }

    public AppConfig Config { get; }

    private static readonly Dictionary<string, string> ProviderUrls = new()
    {
        { "OpenAI", "https://api.openai.com/v1" },
        { "Google", "https://generativelanguage.googleapis.com/v1beta/openai/" },
        { "Zhipu", "https://open.bigmodel.cn/api/paas/v4" },
        { "Deepseek", "https://api.deepseek.com/v1" },
        { "OpenRouter", "https://openrouter.ai/api/v1" },
        { "Custom", "" }
    };

    public List<string> Providers { get; } = new(ProviderUrls.Keys);
    [ObservableProperty]
    private ObservableCollection<string> _modelOptions = new();
    public OpenAiProviderConfiguration PrimaryProvider => Config.AiModels.Provider;

    /// <summary>Web Search 供应商默认 BaseUrl。</summary>
    private static readonly Dictionary<string, string> WebSearchProviderUrls = new()
    {
        { "Tavily", "https://api.tavily.com" },
        { "WebSearchAPI", "https://api.websearchapi.ai" },
        { "Zhipu", "https://open.bigmodel.cn/api/paas/v4" },
        { "Baidu", "https://qianfan.baidubce.com/v2/ai_search/web_search" }
    };

    public List<string> WebSearchProviders { get; } = new(WebSearchProviderUrls.Keys);

    public bool IsBaiduWebSearch => Config.WebSearchProvider == "Baidu";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(IsNotLastStep), nameof(CanGoBack))]
    private int _currentStep;

    public bool IsStep1 => CurrentStep == 0;
    public bool IsStep2 => CurrentStep == 1;
    public bool IsStep3 => CurrentStep == 2;
    public bool IsNotLastStep => CurrentStep < 2;
    public bool CanGoBack => CurrentStep > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private bool _isTestingConnection;

    [ObservableProperty]
    private string _connectionStatus = string.Empty;

    /// <summary>主题按钮当前应显示的图标标记："Moon"=当前 Dark，"Sun"=当前 Light。</summary>
    [ObservableProperty]
    private string _themeIcon = "Sun";

    /// <summary>主题切换：Light↔Dark，立即生效并写入配置。参考 ChatTabViewModel.ToggleTheme。</summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        var next = Config.Theme == "Dark" ? "Light" : "Dark";
        Config.Theme = next;
        ThemeIcon = next == "Dark" ? "Moon" : "Sun";
        App.SetTheme(next);
        _ = SaveAsync();
    }

    /// <summary>语言切换：true=中文，false=English。立即生效并写入配置。</summary>
    public bool IsChineseSelected
    {
        get => Config.Language == "zh-CN";
        set
        {
            var lang = value ? "zh-CN" : "en-US";
            if (Config.Language != lang)
            {
                Config.Language = lang;
                _localizationService?.SwitchLanguage(lang);
                OnPropertyChanged();
            }
        }
    }

    public OnboardingViewModel() : this(null!, null, null, null) { }

    public OnboardingViewModel(IConfigService configService, ILocalizationService? localizationService, IChatService? chatService, IModelCatalogService? modelCatalogService = null)
    {
        _configService = configService;
        _localizationService = localizationService;
        _chatService = chatService;
        _modelCatalogService = modelCatalogService;
        Config = configService?.Load() ?? new AppConfig();

        // 向导语言跟随已保存配置（默认 zh-CN）。
        _localizationService?.SwitchLanguage(Config.Language);

        // 主题按钮图标随已保存主题；订阅全局主题变更以同步（例如设置页也可能改）。
        ThemeIcon = Config.Theme == "Dark" ? "Moon" : "Sun";
        App.ThemeChanged += theme =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                ThemeIcon = theme == "Dark" ? "Moon" : "Sun");
        _ = LoadModelOptionsAsync();
    }

    partial void OnCurrentStepChanged(int value) => _ = SaveAsync();

    /// <summary>Provider 变化时自动带出默认 BaseUrl（Custom 不覆盖用户已填内容）。</summary>
    [RelayCommand]
    private void ApplyProviderDefaultUrl()
    {
        if (ProviderUrls.TryGetValue(PrimaryProvider.ProviderPreset, out var url) && !string.IsNullOrEmpty(url))
        {
            PrimaryProvider.BaseUrl = url;
        }
        _ = LoadModelOptionsAsync();
    }

    [RelayCommand]
    private async Task LoadModelOptionsAsync()
    {
        if (_modelCatalogService == null) return;
        _modelOptionsCts?.Cancel();
        _modelOptionsCts?.Dispose();
        var cts = _modelOptionsCts = new CancellationTokenSource();
        ModelCatalogResult result;
        try
        {
            result = await _modelCatalogService.GetModelsAsync(PrimaryProvider.BaseUrl, PrimaryProvider.ApiKey, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts.IsCancellationRequested || !ReferenceEquals(cts, _modelOptionsCts)) return;
        if (!result.Success) return;

        var selected = new[]
        {
            Config.AiModels.MainConversation.Model,
            Config.AiModels.TitleGeneration.Model,
            Config.AiModels.ContextCompression.Model,
            Config.AiModels.Approval.Model
        };
        // 整体替换 ItemsSource，避免 Clear() 的集合变更让四个下拉框依次丢失当前选择。
        var options = new ObservableCollection<string>(result.Models);
        foreach (var model in selected.Where(model => !string.IsNullOrWhiteSpace(model) && !options.Contains(model)))
            options.Add(model);
        ModelOptions = options;
    }

    /// <summary>Web Search 供应商变化时带出默认 BaseUrl（Custom 保留用户填的端点）。</summary>
    [RelayCommand]
    private void ApplyWebSearchProviderDefaultUrl()
    {
        if (WebSearchProviderUrls.TryGetValue(Config.WebSearchProvider, out var url) && !string.IsNullOrEmpty(url))
        {
            Config.WebSearchBaseUrl = url;
        }
        OnPropertyChanged(nameof(IsBaiduWebSearch));
    }

    private bool CanTestConnection => !IsTestingConnection;

    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private async Task TestConnectionAsync()
    {
        if (_chatService == null) return;
        if (string.IsNullOrWhiteSpace(PrimaryProvider.ApiKey))
        {
            ConnectionStatus = GetString("Onboarding.EnterApiKeyFirst", "> enter api key first");
            return;
        }

        IsTestingConnection = true;
        ConnectionStatus = GetString("Onboarding.Testing", "> consulting the oracle...");
        try
        {
            await SaveAsync();
            _chatService.UpdateConfig(Config);
            var (success, message) = await _chatService.TestConnectionAsync();
            ConnectionStatus = success
                ? GetString("Onboarding.TestSuccess", "> oracle connected ✓")
                : "> " + (message ?? GetString("Onboarding.TestFailed", "connection failed"));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "引导页测试连接失败");
            ConnectionStatus = "> " + ex.Message;
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    [RelayCommand]
    private void Next()
    {
        if (CurrentStep < 2) CurrentStep++;
    }

    [RelayCommand]
    private void Back()
    {
        if (CurrentStep > 0) CurrentStep--;
    }

    /// <summary>跳过或完成：标记引导完成并关窗（App 侧 Closed 处理负责拉起主窗口）。</summary>
    [RelayCommand]
    private async Task FinishAsync()
    {
        Config.OnboardingCompleted = true;
        await SaveAsync();
        RequestClose?.Invoke();
    }

    /// <summary>起手 prompt：预填主窗口聊天输入框后完成引导。</summary>
    [RelayCommand]
    private async Task PickStarterAsync(string? key)
    {
        if (!string.IsNullOrEmpty(key))
        {
            App.PendingStarterPrompt = GetString(key, string.Empty);
        }
        await FinishAsync();
    }

    private async Task SaveAsync()
    {
        try
        {
            if (_configService != null)
            {
                await _configService.SaveAsync(Config);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "引导页保存配置失败");
        }
    }

    private string GetString(string key, string fallback) =>
        _localizationService?.GetString(key, fallback) ?? fallback;
}
