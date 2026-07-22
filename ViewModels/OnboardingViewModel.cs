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
/// 首次启动引导向导（3 步：语言/主题+首个供应商/主模型 → 可选模型分工 → 起手语）。
/// 首个供应商、API Key 与主模型是进入主应用前的最小必填配置。
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
    public OpenAiProviderConfiguration PrimaryProvider => Config.AiModels.Providers[0];

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
        if (Config.AiModels.Providers.Count == 0)
        {
            Config.AiModels.Providers.Add(new OpenAiProviderConfiguration());
        }
        NormalizeRoleAssignments(reuseMainModel: false);

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

        var manual = PrimaryProvider.Models.Where(model => model.IsManual).ToList();
        PrimaryProvider.Models.Clear();
        foreach (var id in result.Models)
        {
            PrimaryProvider.Models.Add(new ProviderModelDescriptor
            {
                Id = id,
                DisplayName = id,
                Capability = id.Contains("embed", StringComparison.OrdinalIgnoreCase)
                    ? ModelCapability.Embedding
                    : ModelCapability.Text
            });
        }
        foreach (var model in manual.Where(model => PrimaryProvider.Models.All(candidate => candidate.Id != model.Id)))
            PrimaryProvider.Models.Add(model);
        PrimaryProvider.ModelsRefreshedAt = DateTimeOffset.Now;

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
            NormalizeRoleAssignments(reuseMainModel: true);
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
        if (CurrentStep == 0 && (string.IsNullOrWhiteSpace(PrimaryProvider.ApiKey)
                                 || string.IsNullOrWhiteSpace(Config.AiModels.MainConversation.Model)))
        {
            ConnectionStatus = "> 请先配置 API Key 和主对话模型";
            return;
        }
        if (CurrentStep < 2) CurrentStep++;
    }

    [RelayCommand]
    private void Back()
    {
        if (CurrentStep > 0) CurrentStep--;
    }

    /// <summary>完成最小配置并请求切换到主窗口。</summary>
    [RelayCommand]
    private async Task FinishAsync()
    {
        if (string.IsNullOrWhiteSpace(PrimaryProvider.ApiKey)
            || string.IsNullOrWhiteSpace(Config.AiModels.MainConversation.Model))
        {
            CurrentStep = 0;
            ConnectionStatus = "> 主对话供应商、API Key 和模型是必填项";
            return;
        }
        NormalizeRoleAssignments(reuseMainModel: true);
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

    private void NormalizeRoleAssignments(bool reuseMainModel)
    {
        var providerId = PrimaryProvider.Id;
        var mainModel = Config.AiModels.MainConversation.Model;
        var roles = new[]
        {
            Config.AiModels.MainConversation,
            Config.AiModels.TitleGeneration,
            Config.AiModels.ContextCompression,
            Config.AiModels.Approval,
            Config.AiModels.SubAgent,
            Config.AiModels.KnowledgeMaintenance
        };
        foreach (var role in roles)
        {
            role.ProviderId = providerId;
            if (reuseMainModel && string.IsNullOrWhiteSpace(role.Model)) role.Model = mainModel;
        }
        if (!string.IsNullOrWhiteSpace(Config.AiModels.Embedding.Model)) Config.AiModels.Embedding.ProviderId = providerId;
        if (!string.IsNullOrWhiteSpace(Config.AiModels.ImageRecognition.Model)) Config.AiModels.ImageRecognition.ProviderId = providerId;
    }
}
