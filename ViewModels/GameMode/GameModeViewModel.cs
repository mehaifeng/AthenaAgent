using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.GameMode;
using Athena.UI.Services.Interfaces;
using Athena.UI.Services.Preview;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels.GameMode;

/// <summary>游戏页面的状态。<see cref="Failed"/> 时 WebView 已经拆掉，Avalonia 的失败面板才看得见（原生 WebView 永远在最上层）。</summary>
public enum PolisPageState
{
    None,
    Loading,
    Ready,
    Failed
}

/// <summary>失败在哪一处被发现（设计稿 12.5 的四处）。</summary>
public enum PolisFailureStage
{
    /// <summary>WebView 创建或挂载失败（WebView2 运行时缺失、WebKitGTK 没装……）。</summary>
    Create,
    /// <summary>页面加载失败（NavigationCompleted 报告失败）。</summary>
    Navigation,
    /// <summary>页面自检失败（没有 WebGL 2、脚本出错、运行时出错）。</summary>
    Page,
    /// <summary>页面迟迟不报"就绪"。</summary>
    Timeout
}

/// <summary>
/// 游戏模式（设计稿第 9–12 节）：中间区域的另一种呈现。它不另存"当前会话"——会话树是唯一的选择来源；
/// 它不另开一个递归文件监听——与工作台共用 <see cref="IWorkspaceWatcherService"/>；它不给聊天服务加回调——
/// 雅典娜的事件从气泡的渲染模型推导（<see cref="PolisEventProjector"/>）。
///
/// 这里是状态机与编排：模式（存进 <c>MainLayoutSettings.GameMode</c>）、页面的生死（惰性创建、失败、保留一段时间再释放）、
/// 城邦的装载（先按快照显示，再后台核对）、外部改动、成果与意图。纯规则都在 <c>Services/GameMode</c>，那里有断言。
/// </summary>
public sealed partial class GameModeViewModel : ViewModelBase, IDisposable
{
    /// <summary>C# → 页面的事件攒批推送间隔（12.3：每次 InvokeScript 都是一次异步往返）。</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>外部改动的去抖窗口，与工作台一致。</summary>
    public static readonly TimeSpan WatcherDebounce = TimeSpan.FromMilliseconds(250);

    /// <summary>导航完成之后多久还没收到"就绪"就算失败（第 4 处）。</summary>
    internal TimeSpan ReadyTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 切回对话之后 WebView 保留多久再释放（12.6"保留多久待定"，M1 取 3 分钟）：来回切换不重新加载场景，
    /// 长时间不回来也不一直占着一个浏览器进程。
    /// </summary>
    internal TimeSpan ReleaseDelay { get; set; } = TimeSpan.FromMinutes(3);

    private static readonly JsonSerializerOptions PageJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly AppConfigurationSession _configuration;
    private readonly OfficePreviewHost _previewHost;
    private readonly IPolisSaveStore _store;
    private readonly IWorkspaceWatcherService _watcher;
    private readonly ILocalizationService _localization;
    private readonly ILogger _logger = Log.ForContext<GameModeViewModel>();
    private readonly string? _home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private IPolisShell? _shell;
    private IPolisPageChannel? _page;
    private readonly List<object> _outbox = new();
    private readonly DispatcherTimer _flushTimer;
    private DispatcherTimer? _readyTimer;
    private DispatcherTimer? _releaseTimer;
    private DispatcherTimer? _watcherTimer;
    private bool _windowVisible = true;
    private bool _disposed;

    // —— 当前的城 ——
    private ConversationSessionItemViewModel? _session;
    private string? _cityKey;
    private WorkspaceProfile? _cityWorkspace;
    private PolisSaveSlot _slot = PolisSaveSlot.Sanctuary;
    private PolisSaveDocument _save = PolisSaveDocument.Empty;
    private PolisIndexDocument? _index;
    private PolisScanResult? _lastScan;
    private PolisFixtureDocument? _city;
    private bool _fogged;
    private CancellationTokenSource? _cityLoad;
    private readonly List<string> _notices = new();
    private readonly List<PolisFsChange> _pendingChanges = new();
    private readonly List<PolisChangeOrigin> _pendingOrigins = new();
    private PolisEventProjector? _projector;
    private bool _projectionDirty;
    private bool _wasWaiting;

    public GameModeViewModel(
        AppConfigurationSession configurationSession,
        OfficePreviewHost previewHost,
        IPolisSaveStore saveStore,
        IWorkspaceWatcherService watcher,
        ILocalizationService localization)
    {
        _configuration = configurationSession ?? throw new ArgumentNullException(nameof(configurationSession));
        _previewHost = previewHost ?? throw new ArgumentNullException(nameof(previewHost));
        _store = saveStore ?? throw new ArgumentNullException(nameof(saveStore));
        _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _isGameMode = _configuration.Current.MainLayout.GameMode;
        _flushTimer = new DispatcherTimer(FlushInterval, DispatcherPriority.Background, (_, _) => Flush());
        _watcher.Changed += OnWorkspaceChanged;
        _watcher.ErrorsDropped += OnWatcherErrorsDropped;
        _watcher.StateChanged += OnWatcherStateChanged;
        _localization.LanguageChanged += OnLanguageChanged;
        _configuration.CurrentChanged += OnConfigurationReplaced;
        if (_isGameMode) EnsurePage();
    }

    /// <summary>整份配置被换掉（设置窗口保存、重新加载）：模式跟着新配置走，不再写回。</summary>
    private void OnConfigurationReplaced(object? sender, AppConfig config)
    {
        if (config.MainLayout.GameMode != IsGameMode) IsGameMode = config.MainLayout.GameMode;
    }

    // ———————————————————————— 模式 ————————————————————————

    /// <summary>中间区域是不是游戏。持久化在 <c>MainLayoutSettings.GameMode</c>；只由用户切换（9.2）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleIconKey))]
    [NotifyPropertyChangedFor(nameof(ToggleTip))]
    private bool _isGameMode;

    /// <summary>非空时视图创建 WebView 并导航到这里；为空时视图拆掉 WebView（惰性创建、释放、失败）。</summary>
    [ObservableProperty]
    private string? _pageUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailure))]
    [NotifyPropertyChangedFor(nameof(IsLoading))]
    private PolisPageState _pageState;

    [ObservableProperty]
    private string _failureReason = string.Empty;

    [ObservableProperty]
    private string _failureDetail = string.Empty;

    public bool HasFailure => PageState == PolisPageState.Failed;

    public bool IsLoading => PageState == PolisPageState.Loading;

    /// <summary>标题栏切换按钮的图标：一个 PathIcon 按 key 切换（不叠两个 IsVisible）。</summary>
    public string ToggleIconKey => IsGameMode ? "AthenaIconConversation" : "AthenaIconGameMode";

    public string ToggleTip => IsGameMode
        ? L("GameMode.Toggle.ToConversation", "Back to conversation mode")
        : L("GameMode.Toggle.ToGame", "Switch to game mode");

    public string FailureTitle => L("GameMode.Failure.Title", "Game mode could not open");

    [RelayCommand]
    private void ToggleMode() => SetGameMode(!IsGameMode, "toggle");

    /// <summary>失败面板上的"回到对话"。不悄悄退回——是用户点的（12.5）。</summary>
    [RelayCommand]
    private void ReturnToConversation() => SetGameMode(false, "return-from-failure");

    [RelayCommand]
    private void Retry()
    {
        _logger.Information("Game mode retry requested after {Stage} failure", _lastFailureStage);
        PageState = PolisPageState.None;
        FailureReason = string.Empty;
        FailureDetail = string.Empty;
        EnsurePage();
    }

    private void SetGameMode(bool on, string reason)
    {
        if (IsGameMode == on) return;
        _configuration.Current.MainLayout.GameMode = on;
        _ = SaveLayoutAsync();
        IsGameMode = on;
        _logger.Information("Center area switched to {Mode} ({Reason})", on ? "game" : "conversation", reason);
    }

    private async Task SaveLayoutAsync()
    {
        try
        {
            await _configuration.SaveNowAsync();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Saving the game-mode layout setting failed");
        }
    }

    partial void OnIsGameModeChanged(bool value)
    {
        if (value)
        {
            _releaseTimer?.Stop();
            if (PageState == PolisPageState.Failed)
            {
                // 上次失败之后又切回来：再试一次（失败面板照样会在失败时出现）
                PageState = PolisPageState.None;
                FailureReason = string.Empty;
                FailureDetail = string.Empty;
            }
            EnsurePage();
            Post(new { type = "visibility", visible = _windowVisible });
            if (_session != null && _cityKey == null) _ = ActivateSessionAsync(_session, voyage: false);
            else if (_watcher.State == WorkspaceWatchState.Unavailable && _cityWorkspace != null)
                _ = RescanAsync("watcher unavailable: rescan on return");
        }
        else
        {
            Post(new { type = "visibility", visible = false });
            Flush();
            ScheduleRelease();
        }
    }

    /// <summary>窗口最小化 / 还原（外壳调用）：不可见时页面停止渲染（12.6）。</summary>
    public void SetWindowVisible(bool visible)
    {
        if (_windowVisible == visible) return;
        _windowVisible = visible;
        if (_page != null) Post(new { type = "visibility", visible = visible && IsGameMode });
    }

    // ———————————————————————— 页面的生死 ————————————————————————

    private PolisFailureStage? _lastFailureStage;

    /// <summary>需要页面而还没有时，给出地址（视图据此惰性创建 WebView）。</summary>
    private void EnsurePage()
    {
        if (!IsGameMode || PageUrl != null || PageState == PolisPageState.Failed) return;
        try
        {
            var url = _previewHost.BuildPolisUrl(_localization.CurrentLanguage);
            // 先置 Loading 再给地址：给地址会让视图同步创建页面，创建失败时 Fail 已经把状态改成 Failed，
            // 顺序反过来就会被这里的 Loading 盖掉——失败面板永远不出现。
            PageState = PolisPageState.Loading;
            PageUrl = url;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            Fail(PolisFailureStage.Create, L("GameMode.Failure.Server", "The local page server could not start"), ex.ToString());
        }
    }

    private void ScheduleRelease()
    {
        if (PageUrl == null) return;
        _releaseTimer ??= new DispatcherTimer(ReleaseDelay, DispatcherPriority.Background, (_, _) => ReleaseIfIdle());
        _releaseTimer.Interval = ReleaseDelay;
        _releaseTimer.Stop();
        _releaseTimer.Start();
    }

    private void ReleaseIfIdle()
    {
        _releaseTimer?.Stop();
        if (IsGameMode) return;
        _logger.Information("Releasing the polis page after {Delay} in conversation mode", ReleaseDelay);
        PageUrl = null;
        PageState = PolisPageState.None;
        _cityKey = null;   // 下次进来按存档重新装载
    }

    /// <summary>视图创建好了 WebView、开始导航（第 1 处之后）。就绪计时从这里开始。</summary>
    public void AttachPage(IPolisPageChannel page)
    {
        _page = page ?? throw new ArgumentNullException(nameof(page));
        _readyTimer ??= new DispatcherTimer(ReadyTimeout, DispatcherPriority.Background, (_, _) => OnReadyTimeout());
        _readyTimer.Interval = ReadyTimeout;
        _readyTimer.Stop();
        _readyTimer.Start();
    }

    /// <summary>视图拆掉了 WebView。</summary>
    public void DetachPage(IPolisPageChannel page)
    {
        if (!ReferenceEquals(_page, page)) return;
        _page = null;
        _readyTimer?.Stop();
        _flushTimer.Stop();
        _outbox.Clear();
        if (PageState is PolisPageState.Loading or PolisPageState.Ready) PageState = PolisPageState.None;
        _cityKey = null;
    }

    /// <summary>第 1 处：WebView 创建或挂载失败。</summary>
    public void OnPageCreateFailed(Exception? error)
        => Fail(PolisFailureStage.Create, L("GameMode.Failure.Create", "The embedded browser (WebView) could not be created on this system"), error?.ToString() ?? string.Empty);

    /// <summary>第 2 处：页面加载失败。</summary>
    public void OnNavigationCompleted(bool success)
    {
        if (success) return;
        Fail(PolisFailureStage.Navigation, L("GameMode.Failure.Navigation", "The game page did not load"), PageUrl ?? string.Empty);
    }

    private void OnReadyTimeout()
    {
        _readyTimer?.Stop();
        if (PageState != PolisPageState.Loading) return;
        Fail(PolisFailureStage.Timeout, L("GameMode.Failure.Timeout", "The game page never reported that it was ready"),
            $"No 'ready' within {ReadyTimeout.TotalSeconds:0} s of navigating to {PageUrl}");
    }

    /// <summary>
    /// 失败：拆掉 WebView（原生 WebView 永远在最上层，不拆它失败面板就看不见），在游戏区显示原因、技术细节和
    /// "回到对话"，并写一条 Warning。不悄悄退回对话模式（12.5）——各平台（包括 Linux）走同一条路。
    /// </summary>
    private void Fail(PolisFailureStage stage, string reason, string detail)
    {
        if (PageState == PolisPageState.Failed) return;
        _logger.Warning("Game mode failed at {Stage}: {Reason}. {Detail}", stage, reason, detail);
        _lastFailureStage = stage;
        _readyTimer?.Stop();
        FailureReason = reason;
        FailureDetail = $"{stage}: {detail}".Trim();
        PageState = PolisPageState.Failed;
        PageUrl = null;
        _page = null;
        _outbox.Clear();
        _cityKey = null;
    }

    /// <summary>页面发来的一条消息（WebMessageReceived）。逐条校验，网页没有"批准"。</summary>
    public void OnPageMessage(string? body)
    {
        var context = new PolisIntentContext(
            _fogged ? null : _cityWorkspace?.DirectoryPath,
            (_shell?.AllSessions ?? Enumerable.Empty<ConversationSessionItemViewModel>()).Select(s => s.ConversationId).ToHashSet(StringComparer.Ordinal),
            _save.Items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal));
        var result = PolisIntents.Validate(body, context);
        if (!result.Accepted)
        {
            _logger.Warning("Rejected a polis page intent: {Reason}", result.Rejection);
            return;
        }
        var intent = result.Intent!;
        switch (intent.Type)
        {
            case PolisIntentType.Ready:
                OnPageReady();
                break;
            case PolisIntentType.Failed:
                Fail(PolisFailureStage.Page, intent.Reason == "webgl"
                        ? L("GameMode.Failure.WebGl", "WebGL 2 is not available here, so the polis cannot be drawn")
                        : L("GameMode.Failure.Script", "The game page ran into an error"),
                    $"{intent.Reason}: {intent.Detail}");
                break;
            case PolisIntentType.SelectConversation:
                _shell?.SelectConversation(intent.ConversationId!);
                break;
            case PolisIntentType.NewCommission:
                if (_shell != null) _ = RunIntentAsync(() => _shell.CreateCommissionAsync(_fogged ? null : _cityWorkspace), "new-commission");
                break;
            case PolisIntentType.OpenFile:
                if (_shell != null) _ = RunIntentAsync(() => _shell.OpenFileAsync(intent.FullPath!), "open-file");
                break;
            case PolisIntentType.AttachFile:
                if (_shell != null) _ = RunIntentAsync(() => _shell.AttachFileAsync(intent.FullPath!), "attach-file");
                break;
            case PolisIntentType.PrefillInput:
                _shell?.PrefillInput(intent.Text!);
                break;
            case PolisIntentType.StopTurn:
                _shell?.StopCurrentTurn();
                break;
            case PolisIntentType.AcceptDelivery:
                _ = DecideItemAsync(intent.ItemId!, PolisItemState.Accepted);
                break;
            case PolisIntentType.ReturnDelivery:
                _ = DecideItemAsync(intent.ItemId!, PolisItemState.Returned);
                break;
            case PolisIntentType.ReadScroll:
                _ = ReadItemAsync(intent.ItemId!);
                break;
            case PolisIntentType.Relocate:
                if (_shell != null && _cityWorkspace != null)
                    _ = RunIntentAsync(() => _shell.PickAndRelocateWorkspaceAsync(_cityWorkspace), "relocate");
                break;
            case PolisIntentType.SavePlayer:
                _save = _save with { Player = new PolisPlayerProgress(intent.X!.Value, intent.Z!.Value, intent.Zoom) };
                _ = PersistSaveAsync();
                break;
        }
    }

    private async Task RunIntentAsync(Func<Task> action, string name)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Polis intent {Intent} failed", name);
        }
    }

    private void OnPageReady()
    {
        _readyTimer?.Stop();
        PageState = PolisPageState.Ready;
        _logger.Information("Polis page ready");
        Post(new { type = "init", tables = PolisLocale.BuildTables(L) });
        Post(new { type = "visibility", visible = _windowVisible && IsGameMode });
        _cityKey = null;
        if (_session != null) _ = ActivateSessionAsync(_session, voyage: false);
        Flush();
    }

    // ———————————————————————— 会话与城 ————————————————————————

    public void AttachShell(IPolisShell shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _shell.WorkspaceRelocated += OnWorkspaceRelocated;
    }

    /// <summary>
    /// 会话切换的第一拍（外壳调用，与选中同一拍）：只做"出港"——要换城时让页面开始航海过场。
    /// 和对话模式的第一拍一样克制：不装城、不扫描、不换订阅。
    /// </summary>
    public void OnSelectionChanged(ConversationSessionItemViewModel? session)
    {
        if (session == null || !IsGameMode || PageState != PolisPageState.Ready) return;
        var key = CityKeyOf(session);
        if (key == _cityKey) return;
        Post(new
        {
            type = "depart",
            caption = session.Workspace == null
                ? L("GameMode.Voyage.Sanctuary", "Sailing to Athena's sanctuary")
                : string.Format(CultureInfo.CurrentCulture, L("GameMode.Voyage.City", "Sailing to {0}"), session.Workspace.Name)
        });
        Flush();
    }

    /// <summary>
    /// 第二拍（<c>OnConversationSurfaceSwapDue</c>，选中动效跑完之后）：换城（同城不换）、雅典娜转向这份委托。
    /// 游戏模式下中间那棵气泡树不建（视图把消息列表的 ItemsSource 摘掉了），所以这一拍没有那 2–3 秒的冻结。
    /// </summary>
    public void OnSurfaceSwapDue(ConversationSessionItemViewModel? session)
    {
        if (session == null) return;
        _ = ActivateSessionAsync(session, voyage: true);
    }

    private static string CityKeyOf(ConversationSessionItemViewModel session) => session.Workspace?.Id ?? "sanctuary";

    private async Task ActivateSessionAsync(ConversationSessionItemViewModel session, bool voyage)
    {
        try
        {
            AttachSession(session);
            if (!IsGameMode || PageState != PolisPageState.Ready) return;
            var key = CityKeyOf(session);
            if (key != _cityKey) await LoadCityAsync(session.Workspace);
            SendFocus();
        }
        catch (OperationCanceledException)
        {
            // 新的切换取消了这一次装载：后来的那次会接着做
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Activating the polis for conversation {ConversationId} failed", session.ConversationId);
        }
    }

    /// <summary>换到这份委托：订阅它的渲染模型，已有的一切算"已经演过"。</summary>
    private void AttachSession(ConversationSessionItemViewModel session)
    {
        if (ReferenceEquals(_session, session) && _projector != null) return;
        DetachSession();
        _session = session;
        session.PropertyChanged += OnSessionPropertyChanged;
        session.Chat.Messages.CollectionChanged += OnMessagesChanged;
        foreach (var message in session.Chat.Messages) WatchMessage(message);
        _wasWaiting = session.IsWaitingForApproval;
    }

    private void DetachSession()
    {
        if (_session == null) return;
        _session.PropertyChanged -= OnSessionPropertyChanged;
        _session.Chat.Messages.CollectionChanged -= OnMessagesChanged;
        foreach (var message in _session.Chat.Messages) UnwatchMessage(message);
        _session = null;
        _projector = null;
    }

    private void SendFocus()
    {
        if (_session == null) return;
        var now = DateTimeOffset.UtcNow;
        var messages = _session.Chat.Messages.ToList();
        _projector = new PolisEventProjector(_fogged ? null : _cityWorkspace?.DirectoryPath, _home, BuildingKeys());
        var last = _projector.LastPlace(messages, now);
        Post(new
        {
            type = "focus",
            conversationId = _session.ConversationId,
            last = last == null ? null : new { category = last.Category, place = last.Place, building = last.Building },
            interrupted = _session.WasInterrupted
        });
        var baseline = _projector.Baseline(messages, now);
        if (baseline.Count > 0) PostEvents(baseline);
        if (_session.IsWaitingForApproval) PostEvents(new[] { new PolisLiveEvent("approval") { At = now, Waiting = true } });
        _wasWaiting = _session.IsWaitingForApproval;
        Flush();
    }

    private IReadOnlySet<string> BuildingKeys()
        => (_city?.Buildings.Select(b => b.Key) ?? Enumerable.Empty<string>())
            .Concat(_city?.Market ?? Enumerable.Empty<string>())
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// 装一座城（设计稿 10.3"先显示，再核对"）：读存档与索引快照 → 有快照就立刻按快照画出来 → 后台有界、可取消地扫描 →
    /// 核对差异（离线改名、空地、藏品找回）→ 推新城、成果与"你离开期间"的报告 → 原子写回存档与快照。
    /// 文件夹不见了：按快照画，笼上雾，不扫描（11.4）。新的切换会取消旧的装载。
    /// </summary>
    private async Task LoadCityAsync(WorkspaceProfile? workspace)
    {
        _cityLoad?.Cancel();
        _cityLoad?.Dispose();
        var cts = new CancellationTokenSource();
        _cityLoad = cts;
        var token = cts.Token;

        _cityWorkspace = workspace;
        _slot = workspace == null ? PolisSaveSlot.Sanctuary : PolisSaveSlot.ForWorkspace(workspace.Id);
        _cityKey = workspace?.Id ?? "sanctuary";
        _lastScan = null;
        _notices.Clear();
        var slot = _slot;
        var read = await _store.LoadAsync(slot, token);
        var index = workspace == null ? null : await _store.LoadIndexAsync(slot, token);
        token.ThrowIfCancellationRequested();
        _save = read.Document;
        _index = index;
        var founding = _save.FoundedAt == null;
        var name = workspace?.Name ?? L("Polis.Public.Sanctuary", "Athena's sanctuary");
        var now = DateTimeOffset.UtcNow;

        if (workspace == null)
        {
            // 神殿：没有文件夹可以建城，一座只有公共建筑的固定场景；它的所有文件操作都算城外（9.1）
            _fogged = false;
            var sanctuary = PolisCityBuilder.FromSnapshot(name, new PolisIndexDocument(), _save, now);
            PostCity(sanctuary, sanctuary: true, founding: false, partial: false);
            PostItems();
            Post(new { type = "fog", missing = false });
            return;
        }

        var root = workspace.DirectoryPath;
        _fogged = !Directory.Exists(root);
        if (index != null || _fogged)
        {
            var snapshot = PolisCityBuilder.FromSnapshot(name, index ?? new PolisIndexDocument(), _save, now);
            PostCity(snapshot, sanctuary: false, founding: false, partial: index == null);
            PostItems();
        }
        Post(new { type = "fog", missing = _fogged, path = _fogged ? root : null });
        Flush();
        if (_fogged)
        {
            _logger.Information("Polis folder is missing for workspace {WorkspaceId}: {Path}", workspace.Id, root);
            return;
        }

        if (index == null)
        {
            // 第一次来、没有快照：先只看顶层（几毫秒），让过场按时靠岸；完整测绘随后到
            var quick = await Task.Run(() => PolisScanner.Scan(PolisFileSystem.Enumerator(root), new PolisScanOptions { MaxEntries = 400, MaxDepth = 1 }, token), token);
            token.ThrowIfCancellationRequested();
            var quickState = PolisCityBuilder.Reconcile(name, quick, _save, null, _ => null, now, token);
            PostCity(quickState.City, sanctuary: false, founding: founding, partial: true);
            Flush();
        }

        await ReconcileAsync(root, name, report: index != null, token);
        if (founding)
        {
            _save = _save with { FoundedAt = DateTimeOffset.UtcNow };
            await PersistSaveAsync();
        }
    }

    /// <summary>后台扫描 + 核对，推送结果并写回存档与快照。</summary>
    private async Task ReconcileAsync(string root, string name, bool report, CancellationToken token)
    {
        var save = _save;
        var index = _index;
        var state = await Task.Run(() =>
        {
            var scan = PolisScanner.Scan(PolisFileSystem.Enumerator(root), PolisScanOptions.Default, token);
            return PolisCityBuilder.Reconcile(name, scan, save, index, PolisFingerprints.ForWorkspace(root, token), DateTimeOffset.UtcNow, token);
        }, token);
        token.ThrowIfCancellationRequested();
        _save = state.Save;
        _index = state.Index;
        _lastScan = state.Scan;
        PostCity(state.City, sanctuary: false, founding: false, partial: false, origins: null);
        PostItems();
        if (report && !state.Report.IsEmpty) PostReport(state.Report);
        if (_projector != null) _projector.BuildingKeys = BuildingKeys();
        Flush();
        await PersistSaveAsync();
        try
        {
            await _store.SaveIndexAsync(_slot, state.Index, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 快照只是缓存：写不进去只少一次"离开期间"的报告
            _logger.Information(ex, "Writing the polis index failed; it is only a cache");
        }
    }

    private async Task RescanAsync(string why)
    {
        if (_cityWorkspace == null || _fogged || PageState != PolisPageState.Ready) return;
        _logger.Information("Rescanning the whole polis: {Why}", why);
        _cityLoad?.Cancel();
        _cityLoad?.Dispose();
        _cityLoad = new CancellationTokenSource();
        try
        {
            await ReconcileAsync(_cityWorkspace.DirectoryPath, _cityWorkspace.Name, report: false, _cityLoad.Token);
        }
        catch (OperationCanceledException)
        {
            // 被下一次重扫或换城取代
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Rescanning the polis failed");
        }
    }

    private void PostCity(PolisFixtureDocument city, bool sanctuary, bool founding, bool partial, IReadOnlyDictionary<string, string>? origins = null)
    {
        _city = city;
        Post(new
        {
            type = "city",
            key = _cityKey,
            city = JsonSerializer.SerializeToElement(city, PolisFixture.JsonOptions),
            sanctuary,
            founding,
            partial,
            player = _save.Player,
            origins
        });
    }

    private void PostItems()
    {
        Post(new
        {
            type = "items",
            items = _save.Items.Select(i => new
            {
                id = i.Id,
                kind = i.Kind,
                title = i.Title,
                state = i.State,
                lost = i.Lost,
                modified = i.ModifiedSinceDelivery,
                path = i.RelativePath,
                createdAt = i.CreatedAt.ToString("O", CultureInfo.InvariantCulture)
            }).ToList()
        });
    }

    private void PostReport(PolisAwayReport report)
    {
        var lines = new List<string>();
        foreach (var change in report.Buildings)
        {
            lines.Add(change.Kind switch
            {
                PolisBuildingChangeKind.Added => F("GameMode.Report.Added", "「{0}」is new: a building went up at the edge of the city", change.Key),
                PolisBuildingChangeKind.Removed => F("GameMode.Report.Removed", "「{0}」is gone and left an empty plot", change.Key),
                PolisBuildingChangeKind.Renamed => F("GameMode.Report.Renamed", "「{0}」was renamed 「{1}」 and kept its plot", change.RenamedFrom ?? string.Empty, change.Key),
                PolisBuildingChangeKind.Grew => F("GameMode.Report.Grew", "「{0}」gained {1} file(s)", change.Key, change.FilesAfter - change.FilesBefore),
                PolisBuildingChangeKind.Shrank => F("GameMode.Report.Shrank", "「{0}」lost {1} file(s)", change.Key, change.FilesBefore - change.FilesAfter),
                _ => F("GameMode.Report.Touched", "Files in 「{0}」 were changed", change.Key)
            });
        }
        foreach (var item in report.Items)
        {
            var title = _save.Items.FirstOrDefault(i => i.Id == item.ItemId)?.Title ?? item.ItemId;
            if (item.Kind == PolisItemRecoveryKind.Lost) lines.Add(F("GameMode.Report.ItemLost", "{0} can no longer be found", title));
            else if (item.Kind == PolisItemRecoveryKind.Moved) lines.Add(F("GameMode.Report.ItemMoved", "{0} moved and was found again", title));
            else if (item.Kind == PolisItemRecoveryKind.Modified) lines.Add(F("GameMode.Report.ItemModified", "{0} was edited after it was delivered", title));
        }
        if (lines.Count == 0) return;
        Post(new { type = "report", title = L("GameMode.Report.Title", "While you were away"), lines });
    }

    private void OnWorkspaceRelocated(object? sender, WorkspaceProfile workspace)
    {
        if (_cityWorkspace?.Id != workspace.Id) return;
        _logger.Information("Polis workspace {WorkspaceId} relocated; surveying the new folder", workspace.Id);
        _cityKey = null;
        if (_session != null) _ = ActivateSessionAsync(_session, voyage: false);
    }

    // ———————————————————————— 外部改动（11.2） ————————————————————————

    private void OnWorkspaceChanged(object? sender, WorkspaceFileChange change)
    {
        // 在监视器线程上：只换算、只入队，去抖与归属在 UI 线程上做
        Dispatcher.UIThread.Post(() => QueueChange(change));
    }

    private void QueueChange(WorkspaceFileChange change)
    {
        if (_disposed || _cityWorkspace == null || change.WorkspaceId != _cityWorkspace.Id || _fogged) return;
        var comparison = PolisPaths.PlatformComparison;
        var relative = PolisPaths.ToRelative(change.Root, change.FullPath, comparison);
        if (string.IsNullOrEmpty(relative)) return;
        var old = change.OldFullPath == null ? null : PolisPaths.ToRelative(change.Root, change.OldFullPath, comparison);
        var at = new DateTimeOffset(change.OccurredAtUtc, TimeSpan.Zero);
        _pendingChanges.Add(new PolisFsChange(relative, change.ChangeType, old, at));
        _pendingOrigins.Add(PolisChanges.Attribute(relative, at, _projector?.Touches ?? Array.Empty<PolisAthenaTouch>(), DateTimeOffset.UtcNow));
        _watcherTimer ??= new DispatcherTimer(WatcherDebounce, DispatcherPriority.Background, (_, _) => _ = ApplyPendingChangesAsync());
        _watcherTimer.Stop();
        _watcherTimer.Start();
    }

    /// <summary>
    /// 一批外部改动：按建筑合并；超过阈值就播一次"城里起了大变化"并整城重扫；否则只重扫受影响的顶层文件夹（增量），
    /// 重算汇总与账本，按座更新。雅典娜做的改动不另加动作（她的动作已经演过）；外部改动只让建筑轻微变化、记到公告板上。
    /// </summary>
    private async Task ApplyPendingChangesAsync()
    {
        _watcherTimer?.Stop();
        if (_pendingChanges.Count == 0 || _cityWorkspace == null) return;
        var changes = _pendingChanges.ToList();
        var origins = _pendingOrigins.ToList();
        _pendingChanges.Clear();
        _pendingOrigins.Clear();
        var batch = PolisChanges.Group(changes, origins);
        if (batch.IsEmpty) return;
        if (batch.FullRescan || _lastScan == null)
        {
            if (batch.FullRescan) AddNotice(L("GameMode.Notice.BigChange", "Something big changed in the city; surveying it again"));
            await RescanAsync(batch.FullRescan ? $"{batch.Changes} changes in one batch" : "no scan to patch yet");
            return;
        }

        var root = _cityWorkspace.DirectoryPath;
        var name = _cityWorkspace.Name;
        var previousBuildings = _city?.Buildings.ToDictionary(b => b.Key, StringComparer.Ordinal) ?? new Dictionary<string, PolisFixtureBuilding>(StringComparer.Ordinal);
        var scan = _lastScan;
        var save = _save;
        foreach (var (from, to) in batch.TopLevelRenames)
        {
            // 运行时收到的顶层改名：建筑原地换牌匾，藏品的引用跟着改（11.2）
            if (!Directory.Exists(Path.Combine(root, to))) continue;
            save = save with
            {
                Ledger = PolisLayoutLedger.Rename(save.Ledger, from, to),
                Items = save.Items.Select(i => i.RelativePath != null && i.RelativePath.StartsWith(from + "/", StringComparison.Ordinal)
                    ? i with { RelativePath = to + i.RelativePath[from.Length..] }
                    : i).ToList()
            };
        }

        try
        {
            var patched = await Task.Run(() => PatchScan(root, scan, batch.DirtyTopLevelNames), CancellationToken.None);
            var state = PolisCityBuilder.Reconcile(name, patched, save, null, PolisFingerprints.ForWorkspace(root), DateTimeOffset.UtcNow);
            _save = state.Save;
            _lastScan = patched;
            _index = state.Index;
            var originMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in batch.ExternalBuildings) originMap[key] = "external";
            foreach (var key in batch.AthenaBuildings) originMap[key] = "athena";
            PostCity(state.City, sanctuary: false, founding: false, partial: false, origins: originMap);
            PostItems();
            foreach (var key in batch.ExternalBuildings.Where(k => !batch.AthenaBuildings.Contains(k)))
            {
                var before = previousBuildings.GetValueOrDefault(key);
                var after = state.City.Buildings.FirstOrDefault(b => b.Key == key);
                AddNotice(DescribeExternal(key, before, after));
            }
            if (_projector != null) _projector.BuildingKeys = BuildingKeys();
            Flush();
            await PersistSaveAsync();
            await _store.SaveIndexAsync(_slot, state.Index, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(ex, "Applying external changes to the polis failed; rescanning the whole city");
            await RescanAsync("incremental update failed");
        }
    }

    /// <summary>
    /// 增量：只重扫受影响的顶层文件夹（各自有界），把它们在上次扫描结果里的那一份换掉；根目录下的文件重列一次。
    /// </summary>
    private static PolisScanResult PatchScan(string root, PolisScanResult previous, IReadOnlySet<string> dirty)
    {
        var enumerate = PolisFileSystem.Enumerator(root);
        var rootEntries = enumerate(string.Empty);
        var topLevel = rootEntries
            .Where(e => e.IsDirectory && !PolisScanner.IsSkippedDirectory(e.Name))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => new PolisTopLevelDirectory(e.Name, e.LastWriteUtc))
            .ToList();
        var topNames = topLevel.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var files = previous.Files
            .Where(f =>
            {
                var top = PolisPaths.TopLevelOf(f.RelativePath);
                // 根目录下的文件重新列；被改动的文件夹整份重扫；消失的文件夹的文件丢掉
                return top.Length > 0 && !dirty.Contains(top) && topNames.Contains(top);
            })
            .ToList();
        files.AddRange(rootEntries
            .Where(e => !e.IsDirectory && !PolisScanner.IsSkippedFile(e.Name))
            .Select(e => new PolisScannedFile(e.Name, e.Size, e.LastWriteUtc)));
        var incomplete = new HashSet<string>(previous.IncompleteTopLevel.Where(n => !dirty.Contains(n) && topNames.Contains(n)), StringComparer.Ordinal);
        foreach (var name in dirty.Where(topNames.Contains))
        {
            var sub = PolisScanner.Scan(relative => enumerate(relative.Length == 0 ? name : name + "/" + relative),
                new PolisScanOptions { MaxEntries = PolisScanOptions.Default.MaxEntries / 4 });
            files.AddRange(sub.Files.Select(f => f with { RelativePath = name + "/" + f.RelativePath }));
            if (sub.Truncated) incomplete.Add(name);
        }
        return previous with
        {
            TopLevelDirectories = topLevel,
            Files = files,
            IncompleteTopLevel = incomplete,
            VisitedEntries = files.Count + topLevel.Count
        };
    }

    private string DescribeExternal(string key, PolisFixtureBuilding? before, PolisFixtureBuilding? after)
    {
        if (before == null && after != null) return F("GameMode.Notice.Added", "「{0}」was added outside the app", key);
        if (before != null && after == null) return F("GameMode.Notice.Removed", "「{0}」was removed outside the app", key);
        if (before != null && after != null && after.Files != before.Files)
        {
            return after.Files > before.Files
                ? F("GameMode.Notice.Grew", "「{0}」gained {1} file(s)", key, after.Files - before.Files)
                : F("GameMode.Notice.Shrank", "「{0}」lost {1} file(s)", key, before.Files - after.Files);
        }
        return F("GameMode.Notice.Touched", "Files in 「{0}」 changed outside the app", key);
    }

    private void AddNotice(string text)
    {
        _notices.Add(text);
        while (_notices.Count > 5) _notices.RemoveAt(0);
        Post(new { type = "notices", notices = _notices.Select(n => new { text = n }).ToList() });
    }

    private void OnWatcherErrorsDropped(object? sender, WorkspaceWatcherErrorEventArgs e)
    {
        // 监听丢了事件：补不回来，整城重扫（11.2）
        Dispatcher.UIThread.Post(() =>
        {
            if (_cityWorkspace?.Id == e.WorkspaceId) _ = RescanAsync($"watcher dropped {e.ErrorCount} event batch(es)");
        });
    }

    private void OnWatcherStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_watcher.State == WorkspaceWatchState.Unavailable && _watcher.WorkspaceId == _cityWorkspace?.Id && _cityWorkspace != null)
                AddNotice(L("GameMode.Notice.WatcherUnavailable", "File watching is unavailable; the city is resurveyed each time you come back"));
        });
    }

    // ———————————————————————— 雅典娜的事件与成果 ————————————————————————

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null) foreach (ChatMessage message in e.NewItems) WatchMessage(message);
        if (e.OldItems != null) foreach (ChatMessage message in e.OldItems) UnwatchMessage(message);
        MarkProjectionDirty();
    }

    private void WatchMessage(ChatMessage message)
    {
        message.PropertyChanged += OnMessagePropertyChanged;
        message.Segments.CollectionChanged += OnSegmentsChanged;
        foreach (var segment in message.Segments) WatchSegment(segment);
    }

    private void UnwatchMessage(ChatMessage message)
    {
        message.PropertyChanged -= OnMessagePropertyChanged;
        message.Segments.CollectionChanged -= OnSegmentsChanged;
        foreach (var segment in message.Segments) UnwatchSegment(segment);
    }

    private void WatchSegment(ChatMessageSegment segment)
    {
        segment.PropertyChanged += OnSegmentPropertyChanged;
        segment.ToolCalls.CollectionChanged += OnToolCallsChanged;
        foreach (var entry in segment.ToolCalls) entry.PropertyChanged += OnToolCallPropertyChanged;
    }

    private void UnwatchSegment(ChatMessageSegment segment)
    {
        segment.PropertyChanged -= OnSegmentPropertyChanged;
        segment.ToolCalls.CollectionChanged -= OnToolCallsChanged;
        foreach (var entry in segment.ToolCalls) entry.PropertyChanged -= OnToolCallPropertyChanged;
    }

    private void OnSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null) foreach (ChatMessageSegment segment in e.NewItems) WatchSegment(segment);
        if (e.OldItems != null) foreach (ChatMessageSegment segment in e.OldItems) UnwatchSegment(segment);
        MarkProjectionDirty();
    }

    private void OnToolCallsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null) foreach (ToolCallEntry entry in e.NewItems) entry.PropertyChanged += OnToolCallPropertyChanged;
        if (e.OldItems != null) foreach (ToolCallEntry entry in e.OldItems) entry.PropertyChanged -= OnToolCallPropertyChanged;
        MarkProjectionDirty();
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatMessage.IsStreaming) or nameof(ChatMessage.IsLoading) or nameof(ChatMessage.DurationMs))
            MarkProjectionDirty();
    }

    private void OnSegmentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatMessageSegment.Text)) MarkProjectionDirty();
    }

    private void OnToolCallPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolCallEntry.Status)) MarkProjectionDirty();
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConversationSessionItemViewModel.IsWaitingForApproval) || _session == null) return;
        var waiting = _session.IsWaitingForApproval;
        if (waiting == _wasWaiting) return;
        _wasWaiting = waiting;
        // 审批状态镜像（7.1"定"）：雅典娜停在门槛前、封印变红。审批本身照旧在原生审批窗口里——网页没有"批准"。
        PostEvents(new[] { new PolisLiveEvent("approval") { At = DateTimeOffset.UtcNow, Waiting = waiting } });
    }

    private void MarkProjectionDirty()
    {
        _projectionDirty = true;
        if (_page != null && !_flushTimer.IsEnabled) _flushTimer.Start();
    }

    /// <summary>算出新增事件；交付的那一回合在这里变成一件成果（存进存档，带颜色等你收下）。</summary>
    private void Project()
    {
        if (!_projectionDirty || _projector == null || _session == null) return;
        _projectionDirty = false;
        var now = DateTimeOffset.UtcNow;
        var events = _projector.Project(_session.Chat.Messages.ToList(), now).ToList();
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i].Type != "deliver" || events[i].MessageId is not { } messageId) continue;
            var item = CreateItem(messageId, now);
            if (item != null) events[i] = events[i] with { ItemId = item.Id };
        }
        if (events.Count > 0) PostEvents(events);
    }

    /// <summary>
    /// 这一回合交出来的成果（7.1"收"）：写进工作区的报告是卷轴、表格是账册、图片是彩绘板；没有落成文件的回答本身也是一卷。
    /// 一回合一件（取最后写的那个交付物）。指纹在后台补上。
    /// </summary>
    private PolisItem? CreateItem(string messageId, DateTimeOffset now)
    {
        if (_session == null || _projector == null) return null;
        if (_save.Items.Any(i => i.SourceMessageId == messageId)) return null;
        var bubble = _session.Chat.Messages.FirstOrDefault(m => m.Id == messageId);
        if (bubble == null) return null;

        var written = _projector.WrittenFiles(bubble)
            .Select(w => w.RelativePath)
            .LastOrDefault(path => KindOfFile(path) != null);
        PolisItem item;
        if (written != null)
        {
            item = new PolisItem
            {
                Id = messageId,
                Kind = KindOfFile(written)!.Value,
                Title = "《" + Path.GetFileNameWithoutExtension(written) + "》",
                RelativePath = written,
                ConversationId = _session.ConversationId,
                SourceMessageId = messageId,
                State = PolisItemState.Pending,
                CreatedAt = now
            };
        }
        else if (bubble.Segments.Any(s => s.IsGeneratedImage))
        {
            item = new PolisItem
            {
                Id = messageId, Kind = PolisItemKind.Painting, Title = L("GameMode.Item.Image", "A painting"),
                ConversationId = _session.ConversationId, SourceMessageId = messageId, State = PolisItemState.Pending, CreatedAt = now
            };
        }
        else
        {
            var text = AnswerText(bubble);
            if (string.IsNullOrWhiteSpace(text)) return null;
            item = new PolisItem
            {
                Id = messageId, Kind = PolisItemKind.Answer, Title = TitleOf(text),
                ConversationId = _session.ConversationId, SourceMessageId = messageId, State = PolisItemState.Pending, CreatedAt = now
            };
        }

        _save = _save with { Items = _save.Items.Append(item).ToList() };
        PostItems();
        _ = FingerprintAndPersistAsync(item);
        return item;
    }

    private async Task FingerprintAndPersistAsync(PolisItem item)
    {
        if (item.RelativePath != null && _cityWorkspace != null)
        {
            var root = _cityWorkspace.DirectoryPath;
            var fingerprint = await Task.Run(() => PolisFingerprints.ForWorkspace(root)(item.RelativePath));
            _save = _save with { Items = _save.Items.Select(i => i.Id == item.Id ? i with { Fingerprint = fingerprint } : i).ToList() };
        }
        await PersistSaveAsync();
    }

    private static PolisItemKind? KindOfFile(string path) => PolisBuildings.ClassifyFile(path) switch
    {
        PolisFileKind.Document => PolisItemKind.Scroll,
        PolisFileKind.Sheet => PolisItemKind.Ledger,
        PolisFileKind.Image => PolisItemKind.Painting,
        _ => null
    };

    private static string AnswerText(ChatMessage bubble)
    {
        var markdown = string.Join("\n\n", bubble.Segments.Where(s => s.IsMarkdown).Select(s => s.Text));
        return string.IsNullOrWhiteSpace(markdown) ? bubble.Content : markdown;
    }

    private static string TitleOf(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim().TrimStart('#', ' ', '*', '-', '>')).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        return line.Length <= 24 ? line : line[..23] + "…";
    }

    private async Task DecideItemAsync(string itemId, PolisItemState state)
    {
        var item = _save.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null) return;
        _save = _save with { Items = _save.Items.Select(i => i.Id == itemId ? i with { State = state, DecidedAt = DateTimeOffset.UtcNow } : i).ToList() };
        PostItems();
        Flush();
        if (state == PolisItemState.Returned)
        {
            // 退回 = 在同一会话里追加一条修改要求：选中它、预填输入框并聚焦，由你写完发出（发消息只走一条路径）
            if (_shell != null && _shell.SelectedSession?.ConversationId != item.ConversationId) _shell.SelectConversation(item.ConversationId);
            _shell?.PrefillInput(F("GameMode.Return.Prefill", "About {0}, please change: ", item.Title));
        }
        await PersistSaveAsync();
    }

    /// <summary>读一件成果：回答在卷轴阅读器里读（正文按需发给页面）；文件在工作台里打开（文件内容不进页面）。</summary>
    private async Task ReadItemAsync(string itemId)
    {
        var item = _save.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null) return;
        if (item.RelativePath != null && _cityWorkspace != null && _shell != null)
        {
            var full = PolisPaths.ToFull(_cityWorkspace.DirectoryPath, item.RelativePath, PolisPaths.PlatformComparison);
            if (full != null) await _shell.OpenFileAsync(full);
            return;
        }
        var session = _shell?.AllSessions.FirstOrDefault(s => s.ConversationId == item.ConversationId);
        var bubble = session?.Chat.Messages.FirstOrDefault(m => m.Id == item.SourceMessageId);
        if (bubble == null) return;
        Post(new { type = "scroll", itemId, title = item.Title, markdown = AnswerText(bubble), closeLabel = L("Polis.Ui.Close", "Close") });
        Flush();
    }

    private async Task PersistSaveAsync()
    {
        try
        {
            await _store.SaveAsync(_slot, _save);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(ex, "Writing the polis save failed for {Slot}", _slot);
        }
    }

    // ———————————————————————— 推送 ————————————————————————

    private void PostEvents(IEnumerable<PolisLiveEvent> events)
    {
        var flushAt = DateTimeOffset.UtcNow;
        Post(new
        {
            type = "events",
            events = events.Select(e => new
            {
                type = e.Type,
                ageMs = Math.Max(0, (flushAt - e.At).TotalMilliseconds),
                turn = e.Turn,
                id = e.Id,
                tool = e.Tool,
                category = e.Category,
                place = e.Place,
                path = e.Path,
                building = e.Building,
                ok = e.Ok,
                reasoning = e.Reasoning,
                agents = e.Agents,
                itemId = e.ItemId,
                waiting = e.Waiting
            }).ToList()
        });
    }

    private void Post(object message)
    {
        if (_page == null) return;
        _outbox.Add(message);
        if (!_flushTimer.IsEnabled) _flushTimer.Start();
    }

    /// <summary>
    /// 攒批推送：一次 InvokeScript 带走这一段时间里的全部消息。整段 JSON 再编码成一个 JS 字符串字面量
    /// （默认编码器转义 &lt; &gt; &amp; ' 与 U+2028/2029），页面 JSON.parse——数据从不被当成脚本拼接（12.4）。
    /// </summary>
    private void Flush()
    {
        _flushTimer.Stop();
        Project();
        if (_page == null || _outbox.Count == 0) return;
        var payload = JsonSerializer.Serialize(_outbox, PageJson);
        _outbox.Clear();
        var script = "window.polis && window.polis.receive(" + JsonSerializer.Serialize(payload) + ")";
        _ = InvokeAsync(_page, script);
    }

    private async Task InvokeAsync(IPolisPageChannel page, string script)
    {
        try
        {
            await page.InvokeScriptAsync(script);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Pushing to the polis page failed");
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ToggleTip));
        OnPropertyChanged(nameof(FailureTitle));
        if (PageState == PolisPageState.Ready)
        {
            Post(new { type = "init", tables = PolisLocale.BuildTables(L) });
            Flush();
        }
    }

    private string L(string key, string fallback) => _localization.GetString(key, fallback);

    private string F(string key, string fallback, params object[] args)
        => string.Format(CultureInfo.CurrentCulture, L(key, fallback), args);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flushTimer.Stop();
        _readyTimer?.Stop();
        _releaseTimer?.Stop();
        _watcherTimer?.Stop();
        _cityLoad?.Cancel();
        _cityLoad?.Dispose();
        DetachSession();
        _watcher.Changed -= OnWorkspaceChanged;
        _watcher.ErrorsDropped -= OnWatcherErrorsDropped;
        _watcher.StateChanged -= OnWatcherStateChanged;
        _localization.LanguageChanged -= OnLanguageChanged;
        _configuration.CurrentChanged -= OnConfigurationReplaced;
        if (_shell != null) _shell.WorkspaceRelocated -= OnWorkspaceRelocated;
    }
}
