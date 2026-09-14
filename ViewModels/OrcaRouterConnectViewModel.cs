using Athena.UI.Services.Interfaces;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

/// <summary>
/// 「使用 OrcaRouter 账号接入」按钮的状态机。供应商与模型窗口、首次引导向导共用同一个实例形状，
/// 差别只在 <c>apply</c> 回调——前者找到或新建一条连接，后者写进引导页那唯一的主连接。
///
/// 三点是刻意的：
/// - 服务不可用（端点配置缺失）时按钮禁用并给出原因，而不是点了没反应。
/// - 浏览器没能自动打开**不是失败**：流程仍在等回调，此时把授权链接原样显示出来供手动打开，
///   这是那种情况下唯一的退路。
/// - 进度回调来自后台线程，必须 Post 回 UI 线程再改可观察属性。
/// </summary>
public partial class OrcaRouterConnectViewModel : ObservableObject, IProgress<OrcaRouterConnectProgress>, IDisposable
{
    private enum StatusKind
    {
        None,
        Awaiting,
        ManualOpen,
        Exchanging,
        Succeeded,
        Canceled,
        Failed,
        Unavailable
    }

    private readonly IOrcaRouterConnectService? _service;
    private readonly ILocalizationService? _localizationService;
    private readonly Func<OrcaRouterConnectResult, Task> _apply;
    private readonly ILogger _logger;

    private CancellationTokenSource? _cancellation;
    private StatusKind _statusKind;
    private string? _failureReason;
    private bool _disposed;

    /// <param name="service">
    /// 接入服务。允许为 null（组合根没注册，或端点配置不可用）——此时入口保持禁用，
    /// 并在状态行里说明原因。
    /// </param>
    /// <param name="apply">拿到 key 之后把它落到配置上。由各自的 ViewModel 决定写到哪条连接。</param>
    public OrcaRouterConnectViewModel(
        IOrcaRouterConnectService? service,
        ILocalizationService? localizationService,
        Func<OrcaRouterConnectResult, Task> apply,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(apply);
        _service = service;
        _localizationService = localizationService;
        _apply = apply;
        _logger = logger ?? Log.ForContext<OrcaRouterConnectViewModel>();

        if (!IsAvailable)
        {
            _statusKind = StatusKind.Unavailable;
            _failureReason = GetString("OrcaRouter.Failure.Unavailable", "OrcaRouter sign-in is unavailable in this build.");
        }
        RefreshStatus();

        if (_localizationService != null) _localizationService.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>端点配置可用时才显示入口；不可用时按钮禁用，状态行给出原因。</summary>
    public bool IsAvailable => _service?.IsAvailable == true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isConnecting;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>失败详情，挂在状态行的 ToolTip 上——正文保持一行。</summary>
    [ObservableProperty]
    private string _statusDetails = string.Empty;

    /// <summary>只有浏览器没能自动打开时才非空；此时它是完成授权的唯一途径。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasManualUrl))]
    [NotifyCanExecuteChangedFor(nameof(CopyManualUrlCommand))]
    private string _manualUrl = string.Empty;

    public bool HasManualUrl => !string.IsNullOrEmpty(ManualUrl);

    private bool CanConnect => IsAvailable && !IsConnecting;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (_service == null || _disposed) return;

        _cancellation?.Dispose();
        var cancellation = _cancellation = new CancellationTokenSource();
        IsConnecting = true;
        ManualUrl = string.Empty;
        SetStatus(StatusKind.Awaiting);

        try
        {
            var result = await _service.ConnectAsync(this, cancellation.Token).ConfigureAwait(true);
            if (_disposed) return;

            if (result.Succeeded && !string.IsNullOrWhiteSpace(result.ApiKey) && result.Endpoints != null)
            {
                await _apply(result).ConfigureAwait(true);
                if (_disposed) return;
                ManualUrl = string.Empty;
                SetStatus(StatusKind.Succeeded);
                return;
            }

            SetStatus(
                result.Failure == OrcaRouterConnectFailure.Canceled ? StatusKind.Canceled : StatusKind.Failed,
                DescribeFailure(result.Failure),
                result.Error);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "OrcaRouter: the connect flow threw");
            if (!_disposed) SetStatus(StatusKind.Failed, ex.Message, ex.Message);
        }
        finally
        {
            if (!_disposed) IsConnecting = false;
            if (ReferenceEquals(_cancellation, cancellation))
            {
                _cancellation = null;
            }
            cancellation.Dispose();
        }
    }

    [RelayCommand(CanExecute = nameof(IsConnecting))]
    private void Cancel() => _cancellation?.Cancel();

    [RelayCommand(CanExecute = nameof(HasManualUrl))]
    private void CopyManualUrl()
    {
        if (string.IsNullOrEmpty(ManualUrl)) return;
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _ = TopLevel.GetTopLevel(desktop.MainWindow)?.Clipboard?.SetTextAsync(ManualUrl);
        }
    }

    /// <summary>进度来自后台线程；可观察属性只能在 UI 线程上改。</summary>
    public void Report(OrcaRouterConnectProgress value)
    {
        if (value == null || _disposed) return;
        Dispatcher.UIThread.Post(() =>
        {
            // 进度是 Post 过来的，可能排在流程收尾之后才跑到——那时它已经过期，
            // 照单执行会把一条早已作废的链接重新亮出来。
            if (_disposed || !IsConnecting) return;
            switch (value.Stage)
            {
                case OrcaRouterConnectStage.Starting:
                    break;
                case OrcaRouterConnectStage.AwaitingAuthorization:
                    // 打不开浏览器不是失败——把链接亮出来，流程还在等这个回调。
                    ManualUrl = value.BrowserOpened ? string.Empty : value.AuthorizationUrl;
                    SetStatus(value.BrowserOpened ? StatusKind.Awaiting : StatusKind.ManualOpen);
                    break;
                case OrcaRouterConnectStage.ExchangingCode:
                    ManualUrl = string.Empty;
                    SetStatus(StatusKind.Exchanging);
                    break;
            }
        });
    }

    private void SetStatus(StatusKind kind, string? reason = null, string? details = null)
    {
        _statusKind = kind;
        _failureReason = reason;
        StatusDetails = details ?? string.Empty;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        Status = _statusKind switch
        {
            StatusKind.Awaiting => GetString("OrcaRouter.Status.Awaiting", "Opened in your browser — waiting for authorization…"),
            StatusKind.ManualOpen => GetString("OrcaRouter.Status.ManualOpen", "Could not open a browser. Open this link manually to continue:"),
            StatusKind.Exchanging => GetString("OrcaRouter.Status.Exchanging", "Authorized — fetching the API key…"),
            StatusKind.Succeeded => GetString("OrcaRouter.Status.Succeeded", "Connected to OrcaRouter."),
            StatusKind.Canceled => GetString("OrcaRouter.Status.Canceled", "Authorization canceled."),
            StatusKind.Unavailable or StatusKind.Failed => string.Format(
                CultureInfo.CurrentCulture,
                GetString("OrcaRouter.Status.Failed", "Could not connect: {0}"),
                _failureReason ?? string.Empty),
            _ => string.Empty
        };
    }

    private string DescribeFailure(OrcaRouterConnectFailure failure) => failure switch
    {
        OrcaRouterConnectFailure.Unavailable => GetString("OrcaRouter.Failure.Unavailable", "OrcaRouter sign-in is unavailable in this build."),
        OrcaRouterConnectFailure.AlreadyRunning => GetString("OrcaRouter.Failure.AlreadyRunning", "An authorization is already in progress."),
        OrcaRouterConnectFailure.TimedOut => GetString("OrcaRouter.Failure.TimedOut", "Timed out waiting for authorization."),
        OrcaRouterConnectFailure.ProviderDenied => GetString("OrcaRouter.Failure.Denied", "Authorization was not granted."),
        OrcaRouterConnectFailure.ExchangeFailed => GetString("OrcaRouter.Failure.Exchange", "Could not fetch the API key."),
        OrcaRouterConnectFailure.MalformedResponse => GetString("OrcaRouter.Failure.Malformed", "The response carried no API key."),
        _ => GetString("OrcaRouter.Failure.Unknown", "Unknown error.")
    };

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed) return;
        // 失败原因本身也是本地化串，语言切换后要跟着换，否则状态行一半中文一半英文。
        if (_statusKind is StatusKind.Failed or StatusKind.Unavailable && _failureReason != null)
        {
            _failureReason = _statusKind == StatusKind.Unavailable
                ? GetString("OrcaRouter.Failure.Unavailable", "OrcaRouter sign-in is unavailable in this build.")
                : _failureReason;
        }
        RefreshStatus();
    });

    private string GetString(string key, string fallback) =>
        _localizationService?.GetString(key, fallback) ?? fallback;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_localizationService != null) _localizationService.LanguageChanged -= OnLanguageChanged;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }
}
