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
    Timeout,
    /// <summary>
    /// 跑着跑着，承载页面的浏览器进程没了（崩溃、超出内存上限被 WebKit 终止）。此后画面是白的、脚本推不进去，
    /// 不报出来就是一个永远白着、切工作区也救不回来的游戏区。
    /// </summary>
    ProcessGone
}

/// <summary>
/// 游戏模式（设计稿第 9–12 节）：中间区域的另一种呈现。它不另存"当前会话"——会话树是唯一的选择来源；
/// 它不另开一个递归文件监听——与工作台共用 <see cref="IWorkspaceWatcherService"/>；它不给聊天服务加回调——
/// 交付与归属从气泡的渲染模型推导（<see cref="PolisDeliveries"/>）。雅典娜不镜像回合（2026-10-13 拿掉了直播）：
/// 页面收不到工具调用、推理和审批事件，只收到这一回合交出来的成果和对话栏的状态。
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
    private readonly ICronTaskReader _cronTasks;
    private readonly PolisAssetStore _assetStore;
    private readonly ILogger _logger = Log.ForContext<GameModeViewModel>();
    private readonly string? _home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// 手动验收用的失败注入（<c>ATHENA_POLIS_FAILURE=create|navigation|webgl|timeout</c>）：在一台一切正常的电脑上
    /// 也能看到四处失败提示各自的样子（见 Docs/GameMode_M1_Acceptance_CN.md）。没设这个环境变量时为 null，
    /// 设了就在启动时写一条 Warning，免得有人忘了它还开着。
    /// </summary>
    private readonly string? _simulatedFailure = SimulatedFailureFromEnvironment();

    private static string? SimulatedFailureFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("ATHENA_POLIS_FAILURE")?.Trim().ToLowerInvariant();
        return value is "create" or "navigation" or "webgl" or "timeout" ? value : null;
    }

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
    /// <summary>
    /// 正在跑的测绘（装城、整城重扫、增量补丁）。同一时刻只跑一次：装城可以抢先（换城取消旧的一切），
    /// 其余的在它跑的期间只记下来，落地之后由 <see cref="ContinueAfterSurvey"/> 补上。
    /// </summary>
    private int _surveysInFlight;
    private string? _rescanAfterSurvey;
    private PolisDeliveries? _deliveries;
    private bool _deliveriesDirty;
    /// <summary>
    /// 游戏刚替这份委托预填了一段话（委托板、居民、神谕、退回）：它显示出来的那一刻，城里的对话栏展开并聚焦，
    /// 让你看一眼再托付。预填可能落在一份还没换上来的委托里（新开的委托要等会话切换的第二拍），所以记下 Id、到时再开。
    /// </summary>
    private string? _prefillFor;

    // —— 积累（M2）：都从存档与会话推导，这里只记"上一次推了什么"，免得每两秒重推一遍 ——
    /// <summary>积累的刷新间隔：会话的完成、别的城亮灯、定时任务的结果，两秒内出现在城里就够了。</summary>
    public static readonly TimeSpan AccumulationInterval = TimeSpan.FromSeconds(2);
    private DispatcherTimer? _accumulationTimer;
    private string? _accumulationKey;
    private IReadOnlyList<PolisCommission> _commissions = Array.Empty<PolisCommission>();
    private IReadOnlyDictionary<string, string> _suggestionPrompts = new Dictionary<string, string>(StringComparer.Ordinal);
    private IReadOnlySet<string> _knownResidents = new HashSet<string>(StringComparer.Ordinal);
    /// <summary>这座城里的居民（会话 Id）：问话的意图只认他们（M-C）。</summary>
    private IReadOnlySet<string> _knownCitizens = new HashSet<string>(StringComparer.Ordinal);
    /// <summary>这座城此刻的居民（会话 Id → 居民）：问话要他的名字、标题、常去的文件夹。</summary>
    private IReadOnlyDictionary<string, PolisCitizen> _citizensById = new Dictionary<string, PolisCitizen>(StringComparer.Ordinal);
    /// <summary>这座城此刻的户（M-D）：推给页面的城里合进它们的民居；换城清空。</summary>
    private IReadOnlyList<PolisHousehold> _households = Array.Empty<PolisHousehold>();
    /// <summary>这座城的第一遍聚户已经做过：之后新立的户才在页面上闪一下、让雅典娜说一句（第一遍是"本来就住在这儿的"）。</summary>
    private bool _householdsBaselined;
    private readonly IPolisTalkService _talk;
    /// <summary>页面关掉、视图模型释放时，还在路上的话一并取消。</summary>
    private readonly CancellationTokenSource _askCancel = new();
    /// <summary>和每个人说过的话（他是谁 → 最近几轮来回，<see cref="PolisTalk.MaxHistoryTurns"/>）：只在内存里，不进存档、不进会话。</summary>
    private readonly Dictionary<string, IReadOnlyList<PolisTalkTurn>> _talkHistory = new(StringComparer.Ordinal);
    /// <summary>流式的半句话：每个说话的人只留最新的那一版（连同这一句的编号），下一次攒批推送时带走（一句话几十个增量，不能每个都推一条）。</summary>
    private readonly Dictionary<string, (int Call, string Text)> _talkPartial = new(StringComparer.Ordinal);
    /// <summary>每个说话的人此刻在说的那一句（编号）：说完、换了新的一句，晚到的半句话就丢掉。</summary>
    private readonly Dictionary<string, int> _talkActive = new(StringComparer.Ordinal);
    private int _talkSeq;
    /// <summary>城里的闲聊此刻有没有一对在说（全城同时只有一对；页面也这么挑，这里再守一道）。</summary>
    private bool _ambientRunning;
    /// <summary>你自己说话之后，附近几个人依次接话的那一轮（第三轮需求 6）；你又开口了就取消上一轮。</summary>
    private CancellationTokenSource? _chorusCancel;
    private readonly IPolisDebateService _debates;
    /// <summary>讲台前此刻的那一场辩论（M-E；讲台只有一座，同一时刻只有一场）。</summary>
    private PolisDebateSession? _debate;
    /// <summary>这一场的调用；作废、换城、释放时取消。</summary>
    private CancellationTokenSource? _debateCancel;
    private PolisContestSummary _debateRecord = PolisContestSummary.Empty;
    private bool _debateRecordLoaded;
    /// <summary>没开成、或者雅典娜 / 裁判没能开口时给页面的一句话（本地化），推一次就清掉。</summary>
    private string? _debateNotice;
    private IReadOnlySet<string> _knownIslands = new HashSet<string>(StringComparer.Ordinal);
    private DateTimeOffset _cityArrivedAt = DateTimeOffset.MinValue;
    // —— 人物关系（第三轮需求 1）：起点按真实的数现推，账本只记事件带来的变化（全局的 Game/relations.json） ——
    private readonly PolisRelationStore _relationStore;
    private PolisRelationLedger _relations = PolisRelationLedger.Empty;
    private bool _relationsLoaded;
    /// <summary>这座城关系里的人：这座城的居民（老居民除外）与十位职业居民；推导居民时一起换。</summary>
    private IReadOnlyList<PolisRelationPerson> _relationPeople = Array.Empty<PolisRelationPerson>();
    private IReadOnlyDictionary<string, PolisRelationPerson> _relationById = new Dictionary<string, PolisRelationPerson>(StringComparer.Ordinal);
    /// <summary>街上的人（含职业居民）：两两之间的关系推给页面（闲聊挑人、几个人接话要用）。</summary>
    private IReadOnlySet<string> _relationStreet = new HashSet<string>(StringComparer.Ordinal);
    /// <summary>上一次推给页面的关系（序列化之后比一比，一样就不推）。</summary>
    private string? _relationsPosted;
    private DispatcherTimer? _relationsSaveTimer;
    private readonly HashSet<string> _noticedRuns = new(StringComparer.Ordinal);

    public GameModeViewModel(
        AppConfigurationSession configurationSession,
        OfficePreviewHost previewHost,
        IPolisSaveStore saveStore,
        IWorkspaceWatcherService watcher,
        ILocalizationService localization,
        ICronTaskReader cronTasks,
        PolisAssetStore assetStore,
        IPolisTalkService talk,
        IPolisDebateService debates,
        PolisRelationStore relations)
    {
        _configuration = configurationSession ?? throw new ArgumentNullException(nameof(configurationSession));
        _previewHost = previewHost ?? throw new ArgumentNullException(nameof(previewHost));
        _store = saveStore ?? throw new ArgumentNullException(nameof(saveStore));
        _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _cronTasks = cronTasks ?? throw new ArgumentNullException(nameof(cronTasks));
        _assetStore = assetStore ?? throw new ArgumentNullException(nameof(assetStore));
        _talk = talk ?? throw new ArgumentNullException(nameof(talk));
        _debates = debates ?? throw new ArgumentNullException(nameof(debates));
        _relationStore = relations ?? throw new ArgumentNullException(nameof(relations));
        _cronTasks.TasksChanged += OnCronTasksChanged;
        _isGameMode = _configuration.Current.MainLayout.GameMode;
        _night = !string.Equals(_configuration.Current.Theme, "Light", StringComparison.OrdinalIgnoreCase);
        _flushTimer = new DispatcherTimer(FlushInterval, DispatcherPriority.Background, (_, _) => Flush());
        _watcher.Changed += OnWorkspaceChanged;
        _watcher.ErrorsDropped += OnWatcherErrorsDropped;
        _watcher.StateChanged += OnWatcherStateChanged;
        _localization.LanguageChanged += OnLanguageChanged;
        _configuration.CurrentChanged += OnConfigurationReplaced;
        if (_simulatedFailure != null)
            _logger.Warning("ATHENA_POLIS_FAILURE={Failure} is set: game mode will simulate this failure for manual acceptance", _simulatedFailure);
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
            // 在对话模式里写了一半的话：带进城里的对话栏
            if (PageState == PolisPageState.Ready && _session != null) PostComposer(includeDraft: true);
            // 在对话模式里换过会话（第二拍照样 AttachSession，但不在游戏模式就不装城）：回来时城要换成选中的那份委托的。
            // 2026-10-12 之前只看 _cityKey == null——页面还没释放（ReleaseDelay 3 分钟内）时 _cityKey 还是上一个工作区的，
            // 切到另一个工作区再回到游戏，城里还是上一个工作区（用户实测）
            if (_session != null && (_cityKey == null || CityKeyOf(_session) != _cityKey)) _ = ActivateSessionAsync(_session, voyage: false);
            else if (_watcher.State == WorkspaceWatchState.Unavailable && _cityWorkspace != null)
                _ = RescanAsync("watcher unavailable: rescan on return");
        }
        else
        {
            Post(new { type = "visibility", visible = false });
            Flush();
            _accumulationTimer?.Stop();
            ScheduleRelease();
        }
    }

    // ———————————————————————— 夜里（跟着应用的深 / 浅色主题） ————————————————————————

    /// <summary>
    /// 应用此刻是不是深色主题（M3 第三轮：游戏加一个夜里，随应用的深 / 浅色切换）。启动时取配置里的主题，
    /// 之后由视图在主题变了的那一刻告诉这里（<see cref="SetNight"/>：<c>ActualThemeVariantChanged</c>，比 <c>App.ThemeChanged</c>
    /// 早 1.2 秒——应用背景的"景深聚焦"过渡和城里从白天走到夜里同时进行）。
    /// </summary>
    private bool _night;

    public bool IsNight => _night;

    /// <summary>主题变了（视图调用）：页面已就绪就推一条 <c>theme</c>，城里一秒多过渡过去；还没就绪的，就绪时带上。</summary>
    public void SetNight(bool night)
    {
        if (_night == night) return;
        _night = night;
        OnPropertyChanged(nameof(IsNight));
        _logger.Debug("Polis follows the app theme: {Theme}", night ? "dark" : "light");
        if (PageState == PolisPageState.Ready)
        {
            Post(new { type = "theme", night });
            Flush();
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
            var url = _previewHost.BuildPolisUrl(_localization.CurrentLanguage, _night);
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
        _ = StampDepartureAsync();
        PageUrl = null;
        PageState = PolisPageState.None;
        _cityKey = null;   // 下次进来按存档重新装载
    }

    /// <summary>视图创建好了 WebView、开始导航（第 1 处之后）。就绪计时从这里开始。</summary>
    public void AttachPage(IPolisPageChannel page)
    {
        _page = page ?? throw new ArgumentNullException(nameof(page));
        if (_simulatedFailure == "create")
        {
            OnPageCreateFailed(new InvalidOperationException("Simulated by ATHENA_POLIS_FAILURE=create (manual acceptance)"));
            return;
        }
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

    /// <summary>第 5 处：页面的浏览器进程中途退出（参数：平台给的原因）。</summary>
    public void OnPageProcessGone(string reason)
        => Fail(PolisFailureStage.ProcessGone, L("GameMode.Failure.ProcessGone", "The game page's browser process quit unexpectedly"), reason);

    /// <summary>第 2 处：页面加载失败。</summary>
    public void OnNavigationCompleted(bool success)
    {
        if (_simulatedFailure == "navigation") success = false;
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
            _save.Items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal))
        {
            KnownSuggestionIds = _suggestionPrompts.Keys.ToHashSet(StringComparer.Ordinal),
            KnownIslandIds = _knownIslands,
            KnownResidentIds = _knownResidents,
            KnownCitizenIds = _knownCitizens,
        };
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
                if (_simulatedFailure == "webgl")
                {
                    Fail(PolisFailureStage.Page, L("GameMode.Failure.WebGl", "WebGL 2 is not available here, so the polis cannot be drawn"),
                        "webgl: simulated by ATHENA_POLIS_FAILURE=webgl (manual acceptance)");
                    break;
                }
                // 就绪超时的手动验收：当作没听见，让计时器自己走到头
                if (_simulatedFailure == "timeout") break;
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
                // 预填会话树上选中的那一份委托（不是"中间正显示的"——第二拍之前两者可能不同）
                if (_shell?.SelectedSession is { } selected) Prefill(selected.ConversationId, intent.Text!);
                break;
            case PolisIntentType.Say:
                Say(intent.ConversationId!, intent.Text!);
                break;
            case PolisIntentType.Draft:
            {
                // 草稿落回它写给的那一份委托自己的输入框（切换的两拍之间，页面可能还在送上一份的草稿）
                var target = _session?.ConversationId == intent.ConversationId
                    ? _session
                    : _shell?.AllSessions.FirstOrDefault(s => s.ConversationId == intent.ConversationId);
                if (target != null && target.Chat.InputText != intent.Text) target.Chat.InputText = intent.Text!;
                break;
            }
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
            case PolisIntentType.PlaceItem:
                ApplyCollection(PolisCollection.Place(_save, intent.ItemId!, intent.Venue!.Value, intent.Slot), "place");
                break;
            case PolisIntentType.UnplaceItem:
                ApplyCollection(PolisCollection.Unplace(_save, intent.ItemId!), "unplace");
                break;
            case PolisIntentType.RemoveItem:
                ApplyCollection(PolisCollection.Remove(_save, intent.ItemId!), "remove");
                break;
            case PolisIntentType.TakeSuggestion:
                if (_shell != null && _suggestionPrompts.TryGetValue(intent.SuggestionId!, out var prompt))
                    _ = RunIntentAsync(() => OpenCommissionWithPrefillAsync(prompt), "take-suggestion");
                break;
            case PolisIntentType.RequestOracle:
                // 神谕（7.3）：只新开一份委托、预填请求——由你按发送；雅典娜调用 create_task 时审批照常弹出，那次批准就是仪式
                if (_shell != null)
                    _ = RunIntentAsync(() => OpenCommissionWithPrefillAsync(PolisAccumulation.OracleRequest(_fogged ? null : _cityWorkspace, L)), "request-oracle");
                break;
            case PolisIntentType.SelectIsland:
                _ = RunIntentAsync(() => SelectIslandAsync(intent.IslandId!), "select-island");
                break;
            case PolisIntentType.MeetResident:
            {
                var slug = PolisResidents.Slug(intent.Resident!.Value);
                if (!_save.MetResidents.Contains(slug, StringComparer.Ordinal))
                {
                    _save = _save with { MetResidents = _save.MetResidents.Append(slug).ToList() };
                    _ = PersistSaveAsync();
                }
                break;
            }
            case PolisIntentType.Chat:
                _ = RunIntentAsync(() => ChatAsync(intent.ConversationId!, intent.Overheard ? PolisTalk.Overheard(intent.Text!, _localization.CurrentLanguage) : intent.Text!, intent.Overheard), "chat");
                break;
            case PolisIntentType.AmbientChat:
                _ = RunIntentAsync(() => AmbientChatAsync(intent.ConversationId!, intent.OtherId!), "ambient-chat");
                break;
            case PolisIntentType.Chorus:
                _ = RunIntentAsync(() => ChorusAsync(intent.Text!, intent.Speakers!), "chorus");
                break;
            case PolisIntentType.CitizenTranscript:
                PostCitizenTranscript(intent.ConversationId!);
                break;
            case PolisIntentType.DebateStart:
                _ = RunIntentAsync(() => StartDebateAsync(intent.Text!), "debate-start");
                break;
            case PolisIntentType.DebateSay:
                _ = RunIntentAsync(() => DebateSayAsync(intent.Text!), "debate-say");
                break;
            case PolisIntentType.DebateEnd:
                EndDebate("page");
                break;
        }
    }

    // —— 城里的竞技：讲台前的辩论（M-E）——
    // 状态机是 PolisDebate（纯函数），这里只做三件事：调用（线程池上，回 UI 线程推页面）、作废晚到的回答（按这一场的 Id）、记战绩。
    // 雅典娜说不出来时替她说一句（这一轮让给你），辩论照常往下走；裁判没给出裁决算平局——没有一处会卡住，也没有一处弹错误框。

    /// <summary>开一场：一次只有一场；每小时最多十场（服务占位）。</summary>
    private async Task StartDebateAsync(string motion)
    {
        if (_disposed) return;
        if (_debate != null && _debate.State != PolisDebateState.Done)
        {
            _logger.Information("Polis debate start ignored: a debate is already running");
            return;
        }
        if (!_debates.TryReserveDebate())
        {
            _debate = null;
            _debateNotice = DebateText("Limited");
            await PostDebateAsync();
            return;
        }
        _debateCancel?.Cancel();
        _debateCancel?.Dispose();
        _debateCancel = new CancellationTokenSource();
        _debate = PolisDebate.Start(motion, DateTimeOffset.Now);
        _logger.Information("Polis debate started ({Length} chars motion)", motion.Length);
        await PostDebateAsync();
    }

    /// <summary>你这一轮：记下 → 她想 → 她说（说不出来替她让一轮）→ 三轮之后裁判。</summary>
    private async Task DebateSayAsync(string text)
    {
        if (_disposed || _debate is not { State: PolisDebateState.AwaitingUser } current || _debateCancel == null) return;
        var id = current.Id;
        var token = _debateCancel.Token;
        _debate = PolisDebate.UserSays(current, text);
        await PostDebateAsync();
        var language = _localization.CurrentLanguage;
        var asked = _debate;
        PolisDebateReply reply;
        try
        {
            reply = await Task.Run(() => _debates.AthenaRespondAsync(asked, language, token), token);
        }
        catch (OperationCanceledException)
        {
            return;   // 作废了（你走开了、换城了）：没有人等她这一句
        }
        if (_disposed || _debate?.Id != id || _debate.State != PolisDebateState.AthenaSpeaking) return;
        var line = reply.Status == PolisDebateCallStatus.Ok && !string.IsNullOrWhiteSpace(reply.Text)
            ? reply.Text!
            : DebateText(reply.Status == PolisDebateCallStatus.NoModel ? "AthenaNoModel" : "AthenaFailed");
        _debate = PolisDebate.AthenaSays(_debate, line);
        await PostDebateAsync();
        // 围观的人起哄（2026-10-13）：她真说了才有（让给你的那一轮没有可评的）；不等它，裁判照常往下走
        if (reply.Status == PolisDebateCallStatus.Ok) _ = RunIntentAsync(() => CrowdReactAsync(id, token), "debate-crowd");
        if (_debate.State == PolisDebateState.Judging) await JudgeDebateAsync(id, token);
    }

    /// <summary>
    /// 辩论时围观的人起哄（2026-10-13，用户：NPC 对回复进行简短评价或者喝彩或者嘘声；每轮一次模型调用）。陪伴角色的 Crowd 池；
    /// 提示词只有世界观、议题和这一轮的两段话（<see cref="PolisTalk.BuildCrowd"/>）。读出两到三句就推 <c>debate-crowd</c>，
    /// 页面把它们放到围观者的头顶；没配模型、超限、失败、读不出来都是一句也没有——不预置台词。晚到的按这一场的 Id 丢掉。
    /// </summary>
    private async Task CrowdReactAsync(string id, CancellationToken token)
    {
        var session = _debate;
        if (_disposed || session == null || session.Id != id) return;
        var you = session.Transcript.LastOrDefault(t => t.Side == "you")?.Text;
        var her = session.Transcript.LastOrDefault(t => t.Side == "athena")?.Text;
        if (you == null || her == null) return;
        var round = session.Transcript.Count(t => t.Side == "athena");
        var prompt = PolisTalk.BuildCrowd(WorldFacts(), session.Motion, round, you, her, _localization.CurrentLanguage);
        PolisTalkReply reply;
        try
        {
            reply = await Task.Run(() => _talk.TalkAsync(PolisTalkPurpose.Crowd, prompt, null, token), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (_disposed || _debate?.Id != id || reply.Status != PolisTalkStatus.Ok) return;
        var reactions = PolisTalk.ParseCrowd(reply.Text);
        if (reactions.Count == 0)
        {
            _logger.Debug("Polis debate crowd reply could not be read");
            return;
        }
        Post(new { type = "debate-crowd", id, round, reactions = reactions.Select(r => new { kind = r.Kind, text = r.Text }).ToList() });
        Flush();
    }

    private async Task JudgeDebateAsync(string id, CancellationToken token)
    {
        var language = _localization.CurrentLanguage;
        var session = _debate!;
        var noVerdict = DebateText("NoVerdict");
        PolisDebateJudgement judgement;
        try
        {
            judgement = await Task.Run(() => _debates.JudgeAsync(session, language, noVerdict, token), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (_disposed || _debate?.Id != id || _debate.State != PolisDebateState.Judging) return;
        var verdict = judgement.Status == PolisDebateCallStatus.Ok && judgement.Verdict != null
            ? judgement.Verdict
            : PolisDebateVerdict.NoVerdict(DebateText(judgement.Status == PolisDebateCallStatus.NoModel ? "JudgeNoModel" : "NoVerdict"));
        _debate = PolisDebate.Judge(_debate, verdict);
        _logger.Information("Polis debate judged: {Votes} of {Jury} for you ({Winner}, judged {Judged})", verdict.VotesForYou, PolisDebate.Jury, verdict.Winner, verdict.Judged);
        try
        {
            _debateRecord = await _debates.RecordAsync(new PolisContestRecord(DateTimeOffset.Now, _debate.Motion, verdict.VotesForYou, verdict.Winner, verdict.Judged), CancellationToken.None);
            _debateRecordLoaded = true;
        }
        catch (Exception ex)
        {
            // 战绩没写进去不影响这一场：页面照样看到裁决；下次记的时候从文件里读到的是没有这一场的战绩
            _logger.Warning(ex, "Could not record the debate result");
        }
        await PostDebateAsync();
    }

    /// <summary>作废这一场（页面说不辩了、你出海了、视图模型释放）。没判完的不记战绩。</summary>
    private void EndDebate(string why)
    {
        if (_debate == null && _debateCancel == null) return;
        _debateCancel?.Cancel();
        _debateCancel?.Dispose();
        _debateCancel = null;
        var wasRunning = _debate is { State: not PolisDebateState.Done };
        _debate = null;
        if (wasRunning) _logger.Information("Polis debate ended before the verdict ({Why})", why);
        if (!_disposed) _ = RunIntentAsync(PostDebateAsync, "debate-end");
    }

    /// <summary>把讲台的现状推给页面：这一场（没有就是 idle）、战绩、要说的那一句。</summary>
    private async Task PostDebateAsync()
    {
        if (_disposed) return;
        if (!_debateRecordLoaded)
        {
            try
            {
                _debateRecord = await _debates.LoadSummaryAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Could not load the debate record");
            }
            _debateRecordLoaded = true;
        }
        if (_disposed) return;
        var d = _debate;
        var notice = _debateNotice;
        _debateNotice = null;
        Post(new
        {
            type = "debate",
            debate = new
            {
                state = d == null ? "idle" : d.State switch
                {
                    PolisDebateState.AwaitingUser => "awaiting",
                    PolisDebateState.AthenaSpeaking => "athena",
                    PolisDebateState.Judging => "judging",
                    _ => "done"
                },
                id = d?.Id,
                motion = d?.Motion,
                round = d?.Round ?? 0,
                rounds = PolisDebate.Rounds,
                jury = PolisDebate.Jury,
                transcript = d?.Transcript.Select(t => new { side = t.Side, text = t.Text }).ToList(),
                verdict = d?.Verdict == null ? null : new { votesForYou = d.Verdict.VotesForYou, winner = d.Verdict.Winner, reason = d.Verdict.Reason, judged = d.Verdict.Judged },
                record = new { wins = _debateRecord.Wins, losses = _debateRecord.Losses, ties = _debateRecord.Ties },
                notice,
            }
        });
        Flush();
    }

    private string DebateText(string what)
    {
        var entry = PolisLocale.Debate.First(d => d.What == what);
        return L(entry.Key, entry.Fallback);
    }

    /// <summary>户名（M-D）：立户那一位的名字加"一家"（2026-10-13 起名字是完整的希腊名，不再拿会话标题当称号）。</summary>
    private string HouseholdLabel(PolisCitizen founder)
    {
        var table = PolisLocale.Citizen.ToDictionary(c => c.JsKey, StringComparer.Ordinal);
        string Text(string jsKey) => L(table[jsKey].Key, table[jsKey].Fallback);
        var who = PolisCitizens.NameFor(founder, Text("namesMale"), Text("namesFemale"), Text("nameNth"));
        return string.Format(CultureInfo.CurrentCulture, Text("householdName"), who);
    }

    /// <summary>户的"样子"（钥匙、户名、人数、新旧）：变了才重推城。</summary>
    private static string HouseholdShape(IReadOnlyList<PolisHousehold> households, DateTimeOffset now)
        => string.Join(";", households.Select(h => $"{h.Key}|{h.Label}|{h.Members.Count}|{PolisBuildings.StateFor(h.LastActive, now)}"));

    /// <summary>
    /// 一位居民那场会话的预览（2026-10-13）：你和雅典娜看得见的那些话（<see cref="PolisCitizens.Transcript"/>），只推给页面的卷轴阅读器。
    /// 会话的消息在内存里（会话树启动时全部恢复），在 UI 线程上取。
    /// </summary>
    private void PostCitizenTranscript(string conversationId)
    {
        if (_disposed || !_citizensById.TryGetValue(conversationId, out var citizen)) return;
        var session = _shell?.AllSessions.FirstOrDefault(s => s.ConversationId == conversationId);
        if (session == null) return;
        var (entries, omitted) = PolisCitizens.Transcript(session.Chat.Messages.ToList());
        Post(new
        {
            type = "citizen-transcript",
            conversationId,
            title = citizen.Title,
            entries = entries.Select(e => new { role = e.Role, text = e.Text }).ToList(),
            omitted
        });
        Flush();
    }

    /// <summary>
    /// 和城里的一个人说一句话（2026-10-13，原来 M-C 的问话）。他的提示词有这座城（<see cref="WorldFacts"/>）、他自己（名字、性子、托付过的那件事）、
    /// 和你说过的几轮，以及按你这一句从他那场会话里挑的几段（<see cref="PolisCitizenAnswers.SelectExcerpts"/>）——所以能闲聊，说到那件事只凭记得的。
    /// 那场会话的消息在内存里（会话树启动时全部恢复，不读库）：在 UI 线程上取一份快照，提示词与调用都在线程池上；
    /// 看得见的字边收边推（<c>talk</c>，<c>done: false</c>，攒批时每人只带最新一版），说完推最后一版（<c>done: true</c>）。
    /// 任何结局都有一句话给他说——没配模型、正忙、超限、失败都是一句本地化的话，不是错误框（<see cref="PolisTalkService"/> 的纪律 1）。
    /// 他的话只回到页面，不进任何会话。
    /// </summary>
    private async Task<PolisTalkReply?> ChatAsync(string who, string line, bool overheard = false, CancellationToken token = default)
    {
        if (who.StartsWith(PolisIntents.ResidentSpeakerPrefix, StringComparison.Ordinal))
            return await ResidentChatAsync(who, line, overheard, token);
        if (_disposed || !_citizensById.TryGetValue(who, out var citizen)) return null;
        var session = _shell?.AllSessions.FirstOrDefault(s => s.ConversationId == who);
        if (session == null) return null;
        var messages = session.Chat.Messages.ToList();
        var table = PolisLocale.Citizen.ToDictionary(c => c.JsKey, StringComparer.Ordinal);
        string Name(string jsKey) => L(table[jsKey].Key, table[jsKey].Fallback);
        var language = _localization.CurrentLanguage;
        var speaker = new PolisSpeaker(
            PolisCitizens.NameFor(citizen, Name("namesMale"), Name("namesFemale"), Name("nameNth")),
            citizen.Female, citizen.Age, PolisTalk.PersonaFor(citizen.Seed, language), PolisTalk.CitizenCalling(citizen, language));
        var world = WorldFacts();
        _talkHistory.TryGetValue(who, out var history);
        history ??= Array.Empty<PolisTalkTurn>();
        var page = _page;
        if (token == default) token = _askCancel.Token;
        var attitude = AttitudeText(who, language);
        var (reply, n) = await StreamLineAsync(who, PolisTalkPurpose.Player,
            () => PolisTalk.BuildChat(world, speaker, PolisCitizenAnswers.SelectExcerpts(messages, line), history, line, language, attitude), token);
        if (reply == null || _disposed || page == null || !ReferenceEquals(page, _page)) return null;
        if (reply.Status == PolisTalkStatus.Ok && !string.IsNullOrWhiteSpace(reply.Text))
        {
            _talkHistory[who] = PolisTalk.Remember(history, line, reply.Text!);
            ApplyRelation(who, overheard ? PolisRelationEvent.Overheard : PolisRelationEvent.Chat);
        }
        var text = reply.Status == PolisTalkStatus.Ok && !string.IsNullOrWhiteSpace(reply.Text) ? reply.Text! : TalkFallback(reply.Status);
        Post(new { type = "talk", who, n, text, done = true, status = reply.Status.ToString().ToLowerInvariant() });
        Flush();
        return reply;
    }

    /// <summary>
    /// 和一位职业居民说一句话（2026-10-13，用户：撤下工具能力，留下职业与他管的那摊事、各自的性子，能像居民一样聊天）。
    /// 提示词 = 这座城 + 他（职业、希腊名、他管的那摊事与性子，<c>Polis.Resident.*.Persona</c>）+ 规矩（没有"你记得的"——他没托付过事）
    /// + 和你说过的几轮。他只是聊天：城里没有一个人能动工具，规矩里写着不答应替谁办事。
    /// </summary>
    private async Task<PolisTalkReply?> ResidentChatAsync(string who, string line, bool overheard = false, CancellationToken token = default)
    {
        var language = _localization.CurrentLanguage;
        if (_disposed || SpeakerOf(who, language) is not { } speaker) return null;
        var world = WorldFacts();
        _talkHistory.TryGetValue(who, out var history);
        history ??= Array.Empty<PolisTalkTurn>();
        var page = _page;
        var attitude = AttitudeText(who, language);
        var (reply, n) = await StreamLineAsync(who, PolisTalkPurpose.Player, () => PolisTalk.BuildChat(world, speaker, null, history, line, language, attitude),
            token == default ? _askCancel.Token : token);
        if (reply == null || _disposed || page == null || !ReferenceEquals(page, _page)) return null;
        if (reply.Status == PolisTalkStatus.Ok && !string.IsNullOrWhiteSpace(reply.Text))
        {
            _talkHistory[who] = PolisTalk.Remember(history, line, reply.Text!);
            ApplyRelation(who, overheard ? PolisRelationEvent.Overheard : PolisRelationEvent.Chat);
        }
        var text = reply.Status == PolisTalkStatus.Ok && !string.IsNullOrWhiteSpace(reply.Text) ? reply.Text! : TalkFallback(reply.Status);
        Post(new { type = "talk", who, n, text, done = true, status = reply.Status.ToString().ToLowerInvariant() });
        Flush();
        return reply;
    }

    /// <summary>
    /// 城里一个能开口的人是谁（提示词里的"他是谁"）：职业居民用他的人设；居民用名字、性子，身份只说那件事办到哪一步（<see cref="PolisTalk.AmbientCalling"/>——
    /// 插话、闲聊不是在回你关于那件事的问题，不带标题）。不认识的返回 null。
    /// </summary>
    private PolisSpeaker? SpeakerOf(string who, string language)
    {
        if (who.StartsWith(PolisIntents.ResidentSpeakerPrefix, StringComparison.Ordinal))
        {
            if (!PolisResidents.TryParse(who[PolisIntents.ResidentSpeakerPrefix.Length..], out var kind)) return null;
            var entry = PolisAccumulation.ResidentEntry(kind);
            return new PolisSpeaker(
                L(entry.GreekKey, entry.GreekFallback), PolisResidents.IsFemale(kind), PolisResidents.AgeOf(kind),
                new PolisPersona(L(entry.NameKey, entry.NameFallback), string.Empty, string.Empty, string.Empty), string.Empty,
                Portrait: L(entry.PersonaKey, entry.PersonaFallback));
        }
        if (!_citizensById.TryGetValue(who, out var citizen)) return null;
        var table = PolisLocale.Citizen.ToDictionary(c => c.JsKey, StringComparer.Ordinal);
        string Name(string jsKey) => L(table[jsKey].Key, table[jsKey].Fallback);
        return new PolisSpeaker(
            PolisCitizens.NameFor(citizen, Name("namesMale"), Name("namesFemale"), Name("nameNth")),
            citizen.Female, citizen.Age, PolisTalk.PersonaFor(citizen.Seed, language), PolisTalk.AmbientCalling(citizen, language));
    }

    /// <summary>
    /// 你自己说了一句（不点名），附近的几个人依次接话（第三轮需求 6，2026-10-13，用户：不指定 NPC 说话时，附近有多个 NPC 就多个都有反应，
    /// 而不是只有一个）。页面挑人、排好先后（越近、越喜欢你、越健谈的越先，厌恶你的不开口）；这里依次让他们说：
    /// 第一位和以前"听见你说话的那一位"一样走 <see cref="ChatAsync"/>（他那场会话里的几段、和你说过的几轮，Player 池，失败了也有一句话）；
    /// 后面的人插一句（<see cref="PolisTalk.BuildChorusLine"/>：听得到前面几位说了什么、知道自己和他们的交情，Chorus 池）。
    /// 下一位开口之前先推一句空的半句话（他头顶出现一串点）。有人没能开口（没配模型、正忙、超限、失败），这一轮就到此为止——插话没有本地台词。
    /// 你又开口了，上一轮没轮到的人不再说（取消）。最后推 <c>chorus-end</c>。
    /// </summary>
    private async Task ChorusAsync(string line, IReadOnlyList<string> speakers)
    {
        if (_disposed || speakers.Count == 0) return;
        _chorusCancel?.Cancel();
        _chorusCancel?.Dispose();
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(_askCancel.Token);
        _chorusCancel = cancel;
        var page = _page;
        var language = _localization.CurrentLanguage;
        var status = "ok";
        var earlier = new List<(string Id, string Name, string Text)>();
        for (var i = 0; i < speakers.Count; i++)
        {
            var who = speakers[i];
            if (cancel.IsCancellationRequested) { status = "cancelled"; break; }
            if (SpeakerOf(who, language) is not { } speaker) continue;
            PolisTalkReply? reply;
            if (i == 0)
            {
                reply = await ChatAsync(who, PolisTalk.Overheard(line, language), overheard: true, cancel.Token);
            }
            else
            {
                // 他还在想：头顶先出现一串点（空的半句话 = 一句新的话开头了）
                Post(new { type = "talk", who, n = 0, text = string.Empty, done = false });
                Flush();
                var attitude = AttitudeText(who, language);
                var ties = earlier.Select(e => (e.Name, e.Text, TiesFrom(who, e.Id, e.Name, language))).ToList();
                var world = WorldFacts();
                var (said, n) = await StreamLineAsync(who, PolisTalkPurpose.Chorus, () => PolisTalk.BuildChorusLine(world, speaker, attitude, line, ties, language), cancel.Token);
                reply = said;
                if (reply != null && reply.Status == PolisTalkStatus.Ok && !string.IsNullOrWhiteSpace(reply.Text) && !_disposed && page != null && ReferenceEquals(page, _page))
                {
                    var text = reply.Text!.Length <= PolisTalk.MaxAmbientChars * 2 ? reply.Text! : reply.Text![..(PolisTalk.MaxAmbientChars * 2)] + "…";
                    Post(new { type = "talk", who, n, text, done = true, status = "ok" });
                    Flush();
                    ApplyRelation(who, PolisRelationEvent.Overheard);
                    reply = reply with { Text = text };
                }
            }
            if (reply == null || _disposed || page == null || !ReferenceEquals(page, _page))
            {
                status = cancel.IsCancellationRequested ? "cancelled" : "failed";
                break;
            }
            if (reply.Status != PolisTalkStatus.Ok || string.IsNullOrWhiteSpace(reply.Text))
            {
                status = reply.Status.ToString().ToLowerInvariant();
                break;
            }
            earlier.Add((who, speaker.Name, reply.Text!));
        }
        if (ReferenceEquals(_chorusCancel, cancel)) _chorusCancel = null;
        cancel.Dispose();
        if (_disposed || page == null || !ReferenceEquals(page, _page)) return;
        Post(new { type = "chorus-end", who = speakers, spoke = earlier.Select(e => e.Id).ToList(), status });
        Flush();
    }

    /// <summary>a 对 b 的交情，一句（插话的提示词用）。</summary>
    private string? TiesFrom(string a, string b, string nameOfB, string language)
    {
        if (!_relationById.TryGetValue(a, out var pa) || !_relationById.TryGetValue(b, out var pb)) return null;
        var (ab, _) = PolisRelations.Between(pa, pb, _relations, DateTimeOffset.UtcNow);
        return PolisTalk.TiesText(PolisRelations.BandOf(ab), nameOfB, language);
    }

    /// <summary>
    /// 说一句话：提示词在线程池上拼、调用在线程池上跑，看得见的字攒批推（<c>talk</c>，<c>done: false</c>）。返回结局；页面没了、取消了返回 null。
    /// 说完的那一版由调用的人推（聊天要带本地化的兜底，闲聊失败了什么都不说）。
    /// </summary>
    private async Task<(PolisTalkReply? Reply, int Call)> StreamLineAsync(string who, PolisTalkPurpose purpose, Func<PolisTalkPrompt> buildPrompt, CancellationToken token)
    {
        var call = ++_talkSeq;
        _talkActive[who] = call;
        _talkPartial.Remove(who);
        void OnVisible(string visible) => Dispatcher.UIThread.Post(() => PostTalkPartial(who, call, visible));
        PolisTalkReply reply;
        try
        {
            reply = await Task.Run(() => _talk.TalkAsync(purpose, buildPrompt(), OnVisible, token), token);
        }
        catch (OperationCanceledException)
        {
            return (null, call);   // 页面没了（离开游戏太久、视图模型释放）：没有人等这句话
        }
        finally
        {
            if (_talkActive.TryGetValue(who, out var active) && active == call) _talkActive.Remove(who);
            _talkPartial.Remove(who);
        }
        return (reply, call);
    }

    /// <summary>
    /// 两位居民在街上碰上了（2026-10-13，用户：NPC 之间的对话只用模型，不预置台词，响应时气泡里是循环的点，回复流式出现）。
    /// 先开口的那位说一句（流式进他头顶），说完立刻让另一位头顶出现点，再带着刚才那句让他接一句；两句都走 <see cref="PolisTalkPurpose.Ambient"/> 池。
    /// 提示词只有世界观、两人的名字与性子、那件事办到哪一步——<b>不带会话标题、不带任何原话</b>（<see cref="PolisTalk.AmbientCalling"/>）。
    /// 没配模型、正忙、超限、失败都不说话（没有本地台词可退），只推 <c>ambient-end</c> 让页面放开这两人、按结局决定多久之后再挑。
    /// </summary>
    private async Task AmbientChatAsync(string a, string b)
    {
        if (_disposed || !_citizensById.TryGetValue(a, out var first) || !_citizensById.TryGetValue(b, out var second)) return;
        var page = _page;
        if (_ambientRunning)
        {
            Post(new { type = "ambient-end", a, b, status = "busy" });
            Flush();
            return;
        }
        _ambientRunning = true;
        try
        {
            var table = PolisLocale.Citizen.ToDictionary(c => c.JsKey, StringComparer.Ordinal);
            string Name(string jsKey) => L(table[jsKey].Key, table[jsKey].Fallback);
            var language = _localization.CurrentLanguage;
            PolisSpeaker Speaker(PolisCitizen c) => new(
                PolisCitizens.NameFor(c, Name("namesMale"), Name("namesFemale"), Name("nameNth")),
                c.Female, c.Age, PolisTalk.PersonaFor(c.Seed, language), PolisTalk.AmbientCalling(c, language));
            var (sa, sb) = (Speaker(first), Speaker(second));
            var world = WorldFacts();
            var token = _askCancel.Token;
            var (tiesA, tiesB) = TiesText(a, b, sb.Name, sa.Name, language);
            var (opening, na) = await StreamLineAsync(a, PolisTalkPurpose.Ambient, () => PolisTalk.BuildAmbientLine(world, sa, sb, null, language, tiesA), token);
            if (opening == null || _disposed || page == null || !ReferenceEquals(page, _page)) return;
            if (opening.Status != PolisTalkStatus.Ok || string.IsNullOrWhiteSpace(opening.Text))
            {
                Post(new { type = "ambient-end", a, b, status = opening.Status.ToString().ToLowerInvariant() });
                Flush();
                return;
            }
            var said = Clip(opening.Text!);
            Post(new { type = "talk", who = a, n = na, text = said, done = true, status = "ok" });
            // 他还在想：头顶先出现一串点（空的半句话 = 一句新的话开头了）
            Post(new { type = "talk", who = b, n = 0, text = string.Empty, done = false });
            Flush();
            var (answer, nb) = await StreamLineAsync(b, PolisTalkPurpose.Ambient, () => PolisTalk.BuildAmbientLine(world, sb, sa, said, language, tiesB), token);
            if (answer == null || _disposed || page == null || !ReferenceEquals(page, _page)) return;
            if (answer.Status == PolisTalkStatus.Ok && !string.IsNullOrWhiteSpace(answer.Text))
            {
                Post(new { type = "talk", who = b, n = nb, text = Clip(answer.Text!), done = true, status = "ok" });
                ApplyAmbientRelation(a, b);
            }
            Post(new { type = "ambient-end", a, b, status = answer.Status.ToString().ToLowerInvariant() });
            Flush();
        }
        finally
        {
            _ambientRunning = false;
        }

        // 闲聊一句话：提示词要它不超过 40 字，模型多说了就截一刀（气泡里放得下）
        static string Clip(string text) => text.Length <= PolisTalk.MaxAmbientChars * 2 ? text : text[..(PolisTalk.MaxAmbientChars * 2)] + "…";
    }

    // ———————————————————————— 人物关系（第三轮需求 1） ————————————————————————
    // 规则在 PolisRelations（纯函数）：起点按真实的数推（会话的结局、来回、脾气、同户、同楼、标题相近、缘分），
    // 这里只做三件事——换城时换一份"关系里的人"、发生了事件记一笔（落盘去抖两秒）、变了就推给页面。

    /// <summary>读一次账本（全局）。读到之前按起点算；读到之后重推一遍。</summary>
    private async Task LoadRelationsAsync()
    {
        var ledger = await _relationStore.LoadAsync(_askCancel.Token);
        if (_disposed) return;
        // 读的这一会儿里已经记了几笔：读到的旧账打底，这几笔按人覆盖上去（它们更新）
        var mine = _relations;
        _relations = new PolisRelationLedger(
            ledger.You.Where(t => mine.You.All(m => !string.Equals(m.Id, t.Id, StringComparison.Ordinal))).Concat(mine.You).ToList(),
            ledger.Pairs.Where(p => mine.Pairs.All(m => m.A != p.A || m.B != p.B)).Concat(mine.Pairs).ToList());
        _relationsLoaded = true;
        _relationsPosted = null;
        PostRelations();
        Flush();
    }

    /// <summary>这座城关系里的人：居民（老居民除外——他们刻在碑上）与十位职业居民。</summary>
    private void UpdateRelationPeople(IReadOnlyList<PolisCitizen> citizens)
    {
        var people = citizens.Where(c => !c.Elder).Select(PolisRelations.CitizenPerson)
            .Concat(PolisResidents.All.Select(PolisRelations.ResidentPerson))
            .ToList();
        _relationPeople = people;
        _relationById = people.ToDictionary(p => p.Id, StringComparer.Ordinal);
        _relationStreet = citizens.Where(c => c.OnStreet).Select(c => c.ConversationId)
            .Concat(PolisResidents.All.Select(k => PolisIntents.ResidentSpeakerPrefix + PolisResidents.Slug(k)))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>算一份关系推给页面（和上一次推的一样就不推）。</summary>
    private void PostRelations()
    {
        if (_disposed || _relationPeople.Count == 0) return;
        var payload = PolisRelations.Payload(_relationPeople, _relationStreet, _relations, DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(payload, PageJson);
        if (string.Equals(json, _relationsPosted, StringComparison.Ordinal)) return;
        _relationsPosted = json;
        Post(new { type = "relations", relations = payload });
    }

    /// <summary>他此刻怎么看你（不在这座城的关系里——比如换城的那一刻——就按寻常）。</summary>
    private PolisRelationView RelationOf(string id)
        => _relationById.TryGetValue(id, out var person)
            ? PolisRelations.ToPlayer(person, _relations, DateTimeOffset.UtcNow)
            : new PolisRelationView(0, PolisRelationBand.Neutral, 3, true, false, Array.Empty<string>());

    /// <summary>他对你的态度，一句话（进和他说话的提示词）。</summary>
    private string AttitudeText(string id, string language) => PolisTalk.AttitudeText(RelationOf(id).Band, language);

    /// <summary>两人之间的交情：a 怎么看 b、b 怎么看 a，各一句（进闲聊的提示词）。</summary>
    private (string? AToB, string? BToA) TiesText(string a, string b, string nameOfB, string nameOfA, string language)
    {
        if (!_relationById.TryGetValue(a, out var pa) || !_relationById.TryGetValue(b, out var pb)) return (null, null);
        var (ab, ba) = PolisRelations.Between(pa, pb, _relations, DateTimeOffset.UtcNow);
        return (PolisTalk.TiesText(PolisRelations.BandOf(ab), nameOfB, language), PolisTalk.TiesText(PolisRelations.BandOf(ba), nameOfA, language));
    }

    /// <summary>记一笔（他对你）：推给页面，两秒后落盘。返回实际改了多少。</summary>
    private double ApplyRelation(string id, PolisRelationEvent e)
    {
        if (_disposed || string.IsNullOrEmpty(id)) return 0;
        (_relations, var applied) = PolisRelations.Apply(_relations, id, e, DateTimeOffset.UtcNow);
        if (applied != 0) _logger.Debug("Polis relation {Event} for {Id}: {Applied:+0.##;-0.##}", e, id, applied);
        PostRelations();
        ScheduleRelationSave();
        return applied;
    }

    /// <summary>两位居民闲聊了一次：彼此 +1。</summary>
    private void ApplyAmbientRelation(string a, string b)
    {
        if (_disposed) return;
        _relations = PolisRelations.ApplyAmbient(_relations, a, b, DateTimeOffset.UtcNow);
        PostRelations();
        ScheduleRelationSave();
    }

    private void ScheduleRelationSave()
    {
        _relationsSaveTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) =>
        {
            _relationsSaveTimer?.Stop();
            _ = RunIntentAsync(SaveRelationsAsync, "relations-save");
        });
        _relationsSaveTimer.Stop();
        _relationsSaveTimer.Start();
    }

    /// <summary>落盘：删掉的会话、淡没了的账先清掉（认识的人是所有会话与十位职业居民——账本是全局的，不只这座城）。</summary>
    private async Task SaveRelationsAsync()
    {
        if (_disposed) return;
        // 还没读到旧账就先读：只拿这一会儿记的几笔整份写下去，等于把旧账抹了
        if (!_relationsLoaded) await LoadRelationsAsync();
        if (_disposed || _shell == null) return;
        var known = _shell.AllSessions.Select(s => s.ConversationId)
            .Concat(PolisResidents.All.Select(k => PolisIntents.ResidentSpeakerPrefix + PolisResidents.Slug(k)))
            .ToHashSet(StringComparer.Ordinal);
        _relations = PolisRelations.Prune(_relations, known, DateTimeOffset.UtcNow);
        await _relationStore.SaveAsync(_relations, _askCancel.Token);
    }

    /// <summary>没能好好说出来时他说的那一句（本地化）：没配模型、正忙、超限、失败各一句。</summary>
    private string TalkFallback(PolisTalkStatus status)
    {
        var replies = PolisLocale.CitizenAsk.ToDictionary(r => r.Status, StringComparer.Ordinal);
        var key = (status == PolisTalkStatus.Ok ? PolisTalkStatus.Failed : status).ToString();
        return L(replies[key].Key, replies[key].Fallback);
    }

    /// <summary>流式的半句话：只留最新的一版，攒批推送时带走（<see cref="Flush"/>）。说完了或换了一句，晚到的丢掉。</summary>
    private void PostTalkPartial(string who, int call, string visible)
    {
        if (_disposed || _page == null || !_talkActive.TryGetValue(who, out var active) || active != call) return;
        _talkPartial[who] = (call, visible);
        if (!_flushTimer.IsEnabled) _flushTimer.Start();
    }

    /// <summary>
    /// 提示词里的这座城（<see cref="PolisTalk.WorldText"/>）：城名、是不是神殿、白天黑夜、人口、建筑（文件夹）名。没有任何消息正文。
    /// </summary>
    private PolisWorldFacts WorldFacts() => new(
        _cityWorkspace?.Name ?? string.Empty,
        _cityWorkspace == null,
        _night,
        _citizensById.Count,
        (_city?.Buildings.Select(b => b.Key) ?? Enumerable.Empty<string>()).Where(k => !PolisHouseholds.IsHouseholdKey(k)).ToList());

    /// <summary>摆放 / 撤下 / 移除：只改存档（7.2"拆方块永远不会变成删文件"），推回页面，落盘。</summary>
    private void ApplyCollection(PolisCollectionResult result, string what)
    {
        if (!result.Ok)
        {
            // 页面只提供空位、只对遗失的给"移除"——走到这里的是过时或越权的请求
            _logger.Information("Polis collection {What} refused: {Reason}", what, result.Rejection);
            PostItems();
            Flush();
            return;
        }
        _save = result.Save;
        PostItems();
        Flush();
        _accumulationKey = null;   // 遗失的藏品没了，委托板的"找找它"也该换掉
        _ = PersistSaveAsync();
    }

    /// <summary>新开一份委托（这座城；神殿里是全局对话）并把一段请求预填进它的输入框——不发送，由你决定。</summary>
    private async Task OpenCommissionWithPrefillAsync(string prompt)
    {
        if (_shell == null || string.IsNullOrWhiteSpace(prompt)) return;
        var session = await _shell.CreateCommissionAsync(_fogged ? null : _cityWorkspace);
        if (session == null)
        {
            _logger.Warning("Polis could not open a new commission for {City}", _cityKey);
            return;
        }
        Prefill(session.ConversationId, prompt);
    }

    /// <summary>
    /// 预填一份委托的输入框（外壳的同一条路径），并记下"显示出来时把对话栏打开"。已经显示着的委托，
    /// <see cref="MainConversationViewModel.InputFocusRequested"/> 当场就把对话栏打开（<see cref="OnChatInputFocusRequested"/>）。
    /// </summary>
    private void Prefill(string conversationId, string text)
    {
        if (_shell == null) return;
        _prefillFor = conversationId;
        _shell.PrefillInput(conversationId, text);
    }

    /// <summary>
    /// 城里对话栏的托付（2026-10-11 起游戏盖住了整个中间区域，输入框也在其中）：只替此刻显示的这一份委托说话，
    /// 走输入框同一条发送路径（外壳的 <see cref="IPolisShell.SubmitInput"/>）。发不出去（她在回答、在压缩、
    /// 页面还停在上一份委托）时一个字都不丢：话留在它写给的那份委托的输入框里，对话栏连同这段话一起退回来。
    /// </summary>
    private void Say(string conversationId, string text)
    {
        if (_shell == null) return;
        if (_session == null || _session.ConversationId != conversationId)
        {
            var written = _shell.AllSessions.FirstOrDefault(s => s.ConversationId == conversationId);
            if (written != null) written.Chat.InputText = text;
            _logger.Information("Polis talk bar sent to {ConversationId}, which is not the commission shown; kept it as that commission's draft", conversationId);
            PostComposer();
            Flush();
            return;
        }
        if (_shell.SubmitInput(conversationId, text))
        {
            _logger.Information("Polis talk bar handed a request ({Length} chars) to {ConversationId}", text.Length, conversationId);
            return;
        }
        _logger.Information("Polis talk bar could not send to {ConversationId} right now (busy or compressing); kept it as the draft", conversationId);
        PostComposer(includeDraft: true, open: true);
        Flush();
    }

    /// <summary>
    /// 海图上选了一座岛（9.1）：选中那个工作区最近的会话——与在会话树上点它完全相同，第一拍出港、第二拍装城；
    /// 岛上还没有委托就新开一份。游戏不另存"当前会话"。
    /// </summary>
    private async Task SelectIslandAsync(string islandId)
    {
        if (_shell == null) return;
        var commissions = PolisAccumulation.ProjectCommissions(_shell.AllSessions);
        var target = PolisIslands.MostRecentConversation(islandId, commissions);
        if (target != null)
        {
            _shell.SelectConversation(target);
            return;
        }
        var workspace = islandId == PolisIslands.SanctuaryId ? null : _shell.Workspaces.FirstOrDefault(w => w.Id == islandId);
        if (islandId != PolisIslands.SanctuaryId && workspace == null) return;
        await _shell.CreateCommissionAsync(workspace);
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
        // 资产清单（AthenaData/Game/assets/manifest.json）：C# 校验过的覆盖随 init 一起给页面；没有清单就是全部程序化
        var manifest = _assetStore.LoadManifest();
        Post(new { type = "init", tables = PolisLocale.BuildTables(L), assets = manifest.Assets.ToDictionary(kv => kv.Key, kv => (object)new
        {
            model = kv.Value.Model,
            scale = kv.Value.Scale,
            rotateY = kv.Value.RotateY,
            offset = kv.Value.Offset,
            animations = kv.Value.Animations,
            rig = kv.Value.Rig
        }, StringComparer.Ordinal) });
        Post(new { type = "visibility", visible = _windowVisible && IsGameMode });
        // 地址里带过一次主题；创建页面之后主题又变过的话以此刻为准（直接到位，不过渡）
        Post(new { type = "theme", night = _night, instant = true });
        // 讲台的战绩（全局，不属于哪座城）：页面刚就绪就推一次，讲台的卡片上才有"你 n 胜 m 负"
        _ = RunIntentAsync(PostDebateAsync, "debate-record");
        // 关系的账本（全局）：读一次；读到之前按起点算（没有事件的变化），读到之后再推一遍
        if (!_relationsLoaded) _ = RunIntentAsync(LoadRelationsAsync, "relations-load");
        _relationsPosted = null;
        _cityKey = null;
        _accumulationKey = null;
        if (_session != null) _ = ActivateSessionAsync(_session, voyage: false);
        _accumulationTimer ??= new DispatcherTimer(AccumulationInterval, DispatcherPriority.Background, (_, _) => RefreshAccumulation(force: false));
        _accumulationTimer.Start();
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
            // 同一座城：不换场景；第一拍可能已经让页面出港（切走又切回），这里让它靠岸
            if (key != _cityKey) await LoadCityAsync(session.Workspace);
            else Post(new { type = "arrive" });
            FocusSession();
        }
        catch (OperationCanceledException)
        {
            // 新的切换取消了这一次装载：后来的那次会接着做
        }
        catch (Exception ex)
        {
            // 装城失败也要靠岸：过场不能卡在海上
            Post(new { type = "arrive" });
            Flush();
            _logger.Warning(ex, "Activating the polis for conversation {ConversationId} failed", session.ConversationId);
        }
    }

    /// <summary>换到这份委托：订阅它的渲染模型，已有的一切算"已经演过"。</summary>
    private void AttachSession(ConversationSessionItemViewModel session)
    {
        if (ReferenceEquals(_session, session) && _deliveries != null) return;
        DetachSession();
        _session = session;
        session.PropertyChanged += OnSessionPropertyChanged;
        session.Chat.Messages.CollectionChanged += OnMessagesChanged;
        foreach (var message in session.Chat.Messages) WatchMessage(message);
        // 城里的对话栏：这份委托在不在回答、能不能托付、附件、预填
        session.Chat.PropertyChanged += OnChatPropertyChanged;
        session.Chat.PendingAttachments.CollectionChanged += OnPendingAttachmentsChanged;
        session.Chat.InputFocusRequested += OnChatInputFocusRequested;
    }

    private void DetachSession()
    {
        if (_session == null) return;
        _session.PropertyChanged -= OnSessionPropertyChanged;
        _session.Chat.Messages.CollectionChanged -= OnMessagesChanged;
        foreach (var message in _session.Chat.Messages) UnwatchMessage(message);
        _session.Chat.PropertyChanged -= OnChatPropertyChanged;
        _session.Chat.PendingAttachments.CollectionChanged -= OnPendingAttachmentsChanged;
        _session.Chat.InputFocusRequested -= OnChatInputFocusRequested;
        _session = null;
        _deliveries = null;
    }

    // ———————————————————————— 城里的对话栏 ————————————————————————

    /// <summary>
    /// 告诉页面这份委托的对话栏该是什么样：在不在回答（托付键换成"请她停下"）、排着队、等审批、在压缩上下文、能不能托付、
    /// 待发送的附件。<paramref name="includeDraft"/>：连同输入框里的字（进场、换委托、预填、退回发不出去的话）；
    /// 平时不带——页面正在打字，回推草稿会和它打架。<paramref name="open"/>：展开并聚焦（预填之后）。
    /// </summary>
    private void PostComposer(bool includeDraft = false, bool open = false)
    {
        if (_session == null) return;
        var chat = _session.Chat;
        Post(new
        {
            type = "composer",
            conversationId = _session.ConversationId,
            busy = chat.IsSending,
            queued = chat.IsQueued,
            waiting = _session.IsWaitingForApproval,
            compressing = chat.IsCompressing,
            canSend = chat.CanAcceptAttachments,
            attachments = chat.PendingAttachments.Select(a => a.DisplayName).ToList(),
            draft = includeDraft ? chat.InputText ?? string.Empty : null,
            open = open ? true : (bool?)null
        });
    }

    private void OnChatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainConversationViewModel.IsSending) or nameof(MainConversationViewModel.IsQueued)
            or nameof(MainConversationViewModel.IsCompressing) or nameof(MainConversationViewModel.CanAcceptAttachments))
            PostComposer();
    }

    private void OnPendingAttachmentsChanged(object? sender, NotifyCollectionChangedEventArgs e) => PostComposer();

    /// <summary>预填之后输入框要焦点：游戏里是城里的对话栏——带着预填好的字展开、聚焦，由你看一眼再托付。</summary>
    private void OnChatInputFocusRequested(object? sender, EventArgs e)
    {
        if (_session == null || !ReferenceEquals(sender, _session.Chat)) return;
        if (_prefillFor == _session.ConversationId) _prefillFor = null;
        if (!IsGameMode || PageState != PolisPageState.Ready) return;
        PostComposer(includeDraft: true, open: true);
        Flush();
    }

    /// <summary>
    /// 换到这份委托：已有的回合算已经交付（不再造成果），正在进行的那一回合从现在起记归属；
    /// 对话栏换成这份委托的——带上它输入框里的字，刚替它预填过（新开的委托、退回）就展开。
    /// 页面那一侧没有"雅典娜转向这份委托"：她不镜像回合，换委托不动她（2026-10-13）。
    /// </summary>
    private void FocusSession()
    {
        if (_session == null) return;
        _deliveries = new PolisDeliveries(_fogged ? null : _cityWorkspace?.DirectoryPath, _home);
        _deliveries.Baseline(_session.Chat.Messages.ToList(), DateTimeOffset.UtcNow);
        var open = _prefillFor == _session.ConversationId;
        if (open) _prefillFor = null;
        PostComposer(includeDraft: true, open: open);
        Flush();
    }

    /// <summary>
    /// 装一座城（设计稿 10.3"先显示，再核对"）：读存档与索引快照 → 有快照就立刻按快照画出来 → 后台有界、可取消地扫描 →
    /// 核对差异（离线改名、空地、藏品找回）→ 推新城、成果与"你离开期间"的报告 → 原子写回存档与快照。
    /// 文件夹不见了：按快照画，笼上雾，不扫描（11.4）。新的切换会取消旧的装载。
    /// </summary>
    private async Task LoadCityAsync(WorkspaceProfile? workspace)
    {
        // 离开上一座城：记下离开的时刻（传令官下次只报这之后的事）；讲台前没辩完的那一场作废（你已经出海了）
        if (_cityKey != null && _cityKey != (workspace?.Id ?? "sanctuary"))
        {
            if (_debate != null) EndDebate("city-changed");
            await StampDepartureAsync();
        }
        _cityLoad?.Cancel();
        _cityLoad?.Dispose();
        var cts = new CancellationTokenSource();
        _cityLoad = cts;
        var token = cts.Token;
        // 攒着的改动与重扫请求都是对上一次装的城说的；这一次从头测绘，它们发生在扫描之前，已经包含在内
        _watcherTimer?.Stop();
        _pendingChanges.Clear();
        _pendingOrigins.Clear();
        _rescanAfterSurvey = null;
        _surveysInFlight++;
        try
        {
            await LoadCityCoreAsync(workspace, token);
        }
        finally
        {
            _surveysInFlight--;
        }
        ContinueAfterSurvey(token);
    }

    private async Task LoadCityCoreAsync(WorkspaceProfile? workspace, CancellationToken token)
    {
        _cityWorkspace = workspace;
        _slot = workspace == null ? PolisSaveSlot.Sanctuary : PolisSaveSlot.ForWorkspace(workspace.Id);
        _cityKey = workspace?.Id ?? "sanctuary";
        _households = Array.Empty<PolisHousehold>();
        _householdsBaselined = false;
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
            Post(new { type = "arrive" });
            await ArriveAsync(null, token);
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
            Post(new { type = "arrive" });
            Flush();
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

        var report = await ReconcileAsync(root, name, report: index != null, token);
        if (founding)
        {
            _save = _save with { FoundedAt = DateTimeOffset.UtcNow };
            await PersistSaveAsync();
        }
        await ArriveAsync(report, token);
    }

    /// <summary>
    /// 到了一座城（10.3、7.3）：传令官播报离开期间的事——定时任务的结果、被打断的委托、外部改动；神谕送来的那一卷作为成果放下；
    /// 记下到达的时刻；积累（橄榄林、居民、委托板、神谕、海图）推给页面。
    /// </summary>
    private async Task ArriveAsync(PolisAwayReport? report, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var tasks = _cronTasks.GetTasks();
        var workspaceId = _cityWorkspace?.Id;
        var commissions = _shell == null ? Array.Empty<PolisCommission>() : PolisAccumulation.ProjectCommissions(_shell.AllSessions);
        var entries = PolisHerald.Compose(tasks, workspaceId, _save.LastVisitedAt, commissions, report,
            id => _save.Items.FirstOrDefault(i => i.Id == id)?.Title, t => PolisOracle.IsOracle(t, workspaceId));
        entries = entries.Select(e => e.Kind == PolisHeraldKind.OracleScroll && e.ConversationId != null
            ? e with { ItemId = CreateOracleItem(e.ConversationId, e.At ?? DateTimeOffset.UtcNow)?.Id }
            : e).ToList();
        foreach (var run in tasks.SelectMany(t => t.RecentRuns)) _noticedRuns.Add(run.RunId);
        if (entries.Count > 0)
        {
            Post(new { type = "report", title = L("GameMode.Report.Title", "While you were away"), lines = PolisAccumulation.HeraldLines(entries, L) });
            PostItems();
        }
        _cityArrivedAt = DateTimeOffset.UtcNow;
        _save = _save with { LastVisitedAt = _cityArrivedAt };
        _accumulationKey = null;
        RefreshAccumulation(force: true);
        Flush();
        await PersistSaveAsync();
    }

    /// <summary>离开这座城：记下时刻（传令官下次只报这之后的事）。在换城之前、页面释放之前调用。</summary>
    private async Task StampDepartureAsync()
    {
        if (_cityKey == null) return;
        _save = _save with { LastVisitedAt = DateTimeOffset.UtcNow };
        await PersistSaveAsync();
    }

    /// <summary>
    /// 每日神谕送来的一卷（7.3"之后每天早上，传令官会送来一卷"）：那次定时运行开的会话里最后一个助手气泡，
    /// 作为一件回答类的成果放在广场上、带颜色等你收下。会话不在树上（或还没有回复）就不放——那一行播报照样可点。
    /// </summary>
    private PolisItem? CreateOracleItem(string conversationId, DateTimeOffset at)
    {
        var session = _shell?.AllSessions.FirstOrDefault(s => s.ConversationId == conversationId);
        var bubble = session?.Chat.Messages.LastOrDefault(m => PolisCommissions.IsAssistantBubble(m) && !m.IsStreaming && !m.IsLoading);
        if (bubble == null) return null;
        var existing = _save.Items.FirstOrDefault(i => i.SourceMessageId == bubble.Id);
        if (existing != null) return existing;
        var item = new PolisItem
        {
            Id = bubble.Id,
            Kind = PolisItemKind.Answer,
            Title = PolisAccumulation.OracleItemTitle(at, L),
            ConversationId = conversationId,
            SourceMessageId = bubble.Id,
            State = PolisItemState.Pending,
            CreatedAt = at
        };
        _save = _save with { Items = _save.Items.Append(item).ToList() };
        return item;
    }

    /// <summary>
    /// 积累的现状推给页面（每两秒看一眼；会话、定时任务、存档没变就什么都不推）：橄榄林、居民、委托板、神谕、海图。
    /// 全部从会话树与存档推导——丢了存档也推得回来，只是"已结识"要再打一次招呼。
    /// </summary>
    private void RefreshAccumulation(bool force)
    {
        if (_disposed || _shell == null || !IsGameMode || PageState != PolisPageState.Ready || _cityKey == null) return;
        var commissions = PolisAccumulation.ProjectCommissions(_shell.AllSessions);
        var workspaces = _shell.Workspaces;
        var tasks = _cronTasks.GetTasks();
        var key = PolisAccumulation.Fingerprint(commissions, _cityKey, _localization.CurrentLanguage, DateTimeOffset.Now.Date,
            string.Join(",", workspaces.Select(w => w.Id + w.DirectoryPath)),
            string.Join(",", tasks.Select(t => t.Id + t.UpdatedAt.UtcTicks + t.RecentRuns.Count + t.IsEnabled)),
            string.Join(",", _save.Items.Select(i => i.Id + (i.Lost ? "L" : "") + i.State)),
            string.Join(",", _save.MetResidents), _city?.Buildings.Count ?? 0);
        if (!force && key == _accumulationKey) return;
        _accumulationKey = key;
        _commissions = commissions;
        var workspaceId = _cityWorkspace?.Id;
        var now = DateTimeOffset.UtcNow;

        var trees = PolisGrove.Derive(commissions, workspaceId);
        Post(new { type = "grove", trees = PolisAccumulation.GroveTrees(trees) });

        var oracle = PolisOracle.Find(tasks, workspaceId);
        var sessionsHere = _shell.AllSessions
            .Where(s => string.Equals(s.Workspace?.Id ?? string.Empty, workspaceId ?? string.Empty, StringComparison.Ordinal))
            .Select(s => (Session: s, Messages: (IReadOnlyList<ChatMessage>)s.Chat.Messages.ToList()))
            .ToList();
        // 十位职业居民每座城一开始就都在（2026-10-13）；按工具推导的那一份只回答"这项能力用过没有"（下面委托板的记忆建议）
        var residents = PolisResidents.Derive(sessionsHere.Select(s => (s.Session.ConversationId, s.Messages)), oracle?.CreatedAt);
        _knownResidents = PolisResidents.All.Select(PolisResidents.Slug).ToHashSet(StringComparer.Ordinal);
        Post(new { type = "residents", residents = PolisAccumulation.Residents(_save.MetResidents.ToList(), L, now) });

        // 居民即会话（M-B）：这座城的每场会话一位居民，街上最多 32 位，按最近动过的先上街
        var byId = commissions.ToDictionary(c => c.ConversationId, StringComparer.Ordinal);
        var citizens = PolisCitizens.Derive(
            sessionsHere.Where(s => byId.ContainsKey(s.Session.ConversationId)).Select(s => new PolisCitizenSource(byId[s.Session.ConversationId], s.Messages)),
            _fogged ? null : _cityWorkspace?.DirectoryPath,
            _home,
            (_city?.Buildings.Select(b => b.Key) ?? Enumerable.Empty<string>()).ToHashSet(StringComparer.Ordinal),
            (_city?.Market ?? Enumerable.Empty<string>()).ToHashSet(StringComparer.Ordinal),
            now,
            ignoreCase: !OperatingSystem.IsLinux());
        // 户与民居（M-D）：没有文件夹可归的居民聚成户、住进民居。户的地写进账本（永不挪位），城（合进民居）先推、居民后推——
        // 居民的家是 b:household:…，页面得先有那座民居才排得出他的日程
        var households = PolisHouseholds.Derive(citizens, HouseholdLabel);
        var ledger = PolisHouseholds.Place(_save.Ledger, households);
        var standingBefore = _save.Ledger.Entries.Where(e => PolisHouseholds.IsHouseholdKey(e.Key) && !e.Vacated).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var householdsChanged = !PolisHouseholds.SameLedger(ledger, _save.Ledger) || HouseholdShape(households, now) != HouseholdShape(_households, now);
        _households = households;
        citizens = PolisHouseholds.Rehome(citizens, households);
        if (householdsChanged && _city != null)
        {
            var founded = households.Where(h => !standingBefore.Contains(h.Key)).Select(h => h.Key).ToList();
            if (!PolisHouseholds.SameLedger(ledger, _save.Ledger))
            {
                _save = _save with { Ledger = ledger };
                _ = PersistSaveAsync();
            }
            // 第一遍（刚到这座城）是本来就住在这儿的人家，不闪、不说；之后新立的户闪一下、雅典娜说一句
            var origins = _householdsBaselined && founded.Count > 0 ? founded.ToDictionary(k => k, _ => "household", StringComparer.Ordinal) : null;
            PostCity(_city, sanctuary: _cityWorkspace == null, founding: false, partial: false, origins: origins);
            if (founded.Count > 0) _logger.Information("Polis households: {Count} new in {City} ({Total} in all)", founded.Count, _cityKey, households.Count);
        }
        _householdsBaselined = true;
        _knownCitizens = citizens.Select(c => c.ConversationId).ToHashSet(StringComparer.Ordinal);
        _citizensById = citizens.ToDictionary(c => c.ConversationId, StringComparer.Ordinal);
        Post(new { type = "citizens", citizens = PolisAccumulation.Citizens(citizens, L) });
        UpdateRelationPeople(citizens);
        PostRelations();

        var suggestions = PolisSuggestions.Generate(new PolisSuggestionInput(
            workspaceId,
            _city?.Buildings ?? Array.Empty<PolisFixtureBuilding>(),
            _city?.Agora,
            _save.Items,
            commissions.Where(c => string.Equals(c.WorkspaceId ?? string.Empty, workspaceId ?? string.Empty, StringComparison.Ordinal)).ToList(),
            oracle != null,
            residents.Any(r => r.Kind == PolisResidentKind.Librarian),
            now));
        var (lines, prompts) = PolisAccumulation.Board(suggestions, L, PolisAccumulation.OracleRequest(_fogged ? null : _cityWorkspace, L));
        _suggestionPrompts = prompts;
        Post(new { type = "board", suggestions = lines });

        Post(new { type = "oracle", oracle = PolisAccumulation.Oracle(oracle, L) });

        var islands = PolisIslands.Build(
            workspaces.Select(w => new PolisIslandSource(w.Id, w.Name, new DateTimeOffset(w.CreatedAt), !Directory.Exists(w.DirectoryPath))).ToList(),
            commissions,
            workspaceId);
        _knownIslands = islands.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        Post(new { type = "islands", islands = PolisAccumulation.Islands(islands) });
        Flush();
    }

    /// <summary>
    /// 定时任务有变化（在别的线程上）：在城里的时候，跑完的一次记到公告板上（神谕送来的那一卷当场放下）；神庙的状态跟着换。
    /// </summary>
    private void OnCronTasksChanged(object? sender, CronTaskListChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || _cityKey == null || PageState != PolisPageState.Ready) return;
            var workspaceId = _cityWorkspace?.Id;
            foreach (var task in _cronTasks.GetTasks().Where(t => string.Equals(t.WorkspaceId ?? string.Empty, workspaceId ?? string.Empty, StringComparison.Ordinal)))
            {
                foreach (var run in task.RecentRuns.Where(r => r.IsTerminal && (r.CompletedAt ?? r.ScheduledFor) >= _cityArrivedAt))
                {
                    if (!_noticedRuns.Add(run.RunId)) continue;
                    if (run.State == CronRunState.Succeeded && PolisOracle.IsOracle(task, workspaceId) && run.ConversationId != null)
                    {
                        if (CreateOracleItem(run.ConversationId, run.CompletedAt ?? DateTimeOffset.UtcNow) != null) { PostItems(); _ = PersistSaveAsync(); }
                    }
                    var kind = run.State switch
                    {
                        CronRunState.Succeeded => "CronSucceeded",
                        CronRunState.Failed => "CronFailed",
                        CronRunState.Skipped => "CronSkipped",
                        _ => "CronInterrupted"
                    };
                    var entry = PolisLocale.Herald.First(h => h.Kind == kind);
                    AddNotice(string.Format(CultureInfo.CurrentCulture, L(entry.Key, entry.Fallback), task.Name, run.Error?.Split('\n')[0] ?? string.Empty));
                }
            }
            _accumulationKey = null;
            RefreshAccumulation(force: true);
        });
    }

    /// <summary>后台扫描 + 核对，推送结果并写回存档与快照。返回"你离开期间"的报告（不要报告时为 null），由到达时的传令官播报。</summary>
    private async Task<PolisAwayReport?> ReconcileAsync(string root, string name, bool report, CancellationToken token)
    {
        var save = _save;
        var index = _index;
        var slot = _slot;
        var scanned = SurveyScanned;
        var state = await Task.Run(() =>
        {
            var scan = PolisScanner.Scan(PolisFileSystem.Enumerator(root), PolisScanOptions.Default, token);
            scanned?.Invoke(root);
            return PolisCityBuilder.Reconcile(name, scan, save, index, PolisFingerprints.ForWorkspace(root, token), DateTimeOffset.UtcNow, token);
        }, token);
        token.ThrowIfCancellationRequested();
        _save = MergeReconciled(_save, state.Save);
        _index = state.Index;
        _lastScan = state.Scan;
        PostCity(state.City, sanctuary: false, founding: false, partial: false, origins: null);
        PostItems();
        Flush();
        await PersistSaveAsync();
        try
        {
            // 用开始时的槽位：上面那次 await 期间可能已经换了城，这份快照属于这一座
            await _store.SaveIndexAsync(slot, state.Index, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 快照只是缓存：写不进去只少一次"离开期间"的报告
            _logger.Information(ex, "Writing the polis index failed; it is only a cache");
        }
        return report && !state.Report.IsEmpty ? state.Report : null;
    }

    /// <summary>
    /// 测试缝：后台测绘扫完文件夹、还没核对之前调用（在线程池上，参数是工作区根目录），装城、重扫、增量补丁都经过这里。
    /// 无头测试用它把一次测绘卡在"读过文件夹、还没落地"——外部改动与换城恰好落在这段时间里的那种竞争。
    /// </summary>
    internal Action<string>? SurveyScanned { get; set; }

    /// <summary>她这一回合碰过的地方（测试用：等归属记下了再写盘）。</summary>
    internal IReadOnlyList<PolisAthenaTouch> AthenaTouches => _deliveries?.Touches ?? Array.Empty<PolisAthenaTouch>();

    private async Task RescanAsync(string why)
    {
        if (_cityWorkspace == null || _fogged || PageState != PolisPageState.Ready) return;
        if (_surveysInFlight > 0)
        {
            // 不取消正在跑的那次，落地之后再整城扫一遍。取消它要付两种代价：装城那一次带着"你离开期间"的报告，
            // 会被一个不报告的重扫顶掉；装城的前半段还没读进这座城的存档，这时起的重扫会拿上一座城的账本来核对这一座。
            _rescanAfterSurvey ??= why;
            _logger.Debug("Polis rescan ({Why}) deferred until the survey in flight lands", why);
            return;
        }
        _logger.Information("Rescanning the whole polis: {Why}", why);
        _cityLoad?.Cancel();
        _cityLoad?.Dispose();
        _cityLoad = new CancellationTokenSource();
        var token = _cityLoad.Token;
        _surveysInFlight++;
        try
        {
            await ReconcileAsync(_cityWorkspace.DirectoryPath, _cityWorkspace.Name, report: false, token);
        }
        catch (OperationCanceledException)
        {
            // 换城取代了这一次：新的装城会从头测绘
            return;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Rescanning the polis failed");
            return;
        }
        finally
        {
            _surveysInFlight--;
        }
        ContinueAfterSurvey(token);
    }

    /// <summary>一次测绘落地之后：先补它跑的期间被要求的整城重扫，否则补攒下的外部改动。</summary>
    private void ContinueAfterSurvey(CancellationToken token)
    {
        if (_disposed || _surveysInFlight > 0 || token.IsCancellationRequested) return;
        if (_rescanAfterSurvey is { } why)
        {
            _rescanAfterSurvey = null;
            _ = RescanAsync(why);
        }
        else if (_pendingChanges.Count > 0)
        {
            _ = ApplyPendingChangesAsync();
        }
    }

    private void PostCity(PolisFixtureDocument city, bool sanctuary, bool founding, bool partial, IReadOnlyDictionary<string, string>? origins = null)
    {
        // C# 自己记的城只有文件夹的建筑（委托板、居民的家、外部改动都按它算）；推给页面的那一份合进户的民居（M-D）
        _city = city;
        var shown = PolisHouseholds.WithHouseholds(city, _households, _save.Ledger, DateTimeOffset.UtcNow);
        Post(new
        {
            type = "city",
            key = _cityKey,
            city = JsonSerializer.SerializeToElement(shown, PolisFixture.JsonOptions),
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
                createdAt = i.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                placement = i.Placement == null ? null : new { venue = PolisCollection.Slug(i.Placement.Venue), slot = i.Placement.Slot }
            }).ToList()
        });
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
        _pendingOrigins.Add(PolisChanges.Attribute(relative, at, _deliveries?.Touches ?? Array.Empty<PolisAthenaTouch>(), DateTimeOffset.UtcNow));
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
        if (_disposed || _pendingChanges.Count == 0 || _cityWorkspace == null) return;
        if (_surveysInFlight > 0)
        {
            // 留在队里，测绘落地之后补（ContinueAfterSurvey）。装城的核对还没落地时没有可打的底稿，
            // 以前这里会起一次整城重扫、把那次核对连同"你离开期间"的报告一起取消掉。
            _logger.Debug("Deferring {Count} external polis change(s) until the survey in flight lands", _pendingChanges.Count);
            return;
        }
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
        var slot = _slot;
        // 换城会取消它：补丁是按这一座城的账本算的，一个字节都不能落到下一座城的存档上
        var token = _cityLoad?.Token ?? CancellationToken.None;
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

        var failed = false;
        var scanned = SurveyScanned;
        _surveysInFlight++;
        try
        {
            // 核对藏品要读盘、算哈希（每件已交付的成果都看一眼，单件最多 64 MB）：和扫描一起放在线程池上
            var state = await Task.Run(() =>
            {
                var patched = PatchScan(root, scan, batch.DirtyTopLevelNames);
                scanned?.Invoke(root);
                return PolisCityBuilder.Reconcile(name, patched, save, null, PolisFingerprints.ForWorkspace(root, token), DateTimeOffset.UtcNow, token);
            }, token);
            token.ThrowIfCancellationRequested();
            _save = MergeReconciled(_save, state.Save);
            _lastScan = state.Scan;
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
            Flush();
            await PersistSaveAsync();
            await _store.SaveIndexAsync(slot, state.Index, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // 换城取代了这一次：新的装城会从头测绘
            _logger.Debug("Dropped a polis change patch for {Root}: the city changed while it ran", root);
            return;
        }
        catch (Exception ex)
        {
            // 补丁打不上就整城重扫（在 finally 让出"正在测绘"之后，否则重扫会把自己排到自己后面）
            _logger.Warning(ex, "Applying external changes to the polis failed; rescanning the whole city");
            failed = true;
        }
        finally
        {
            _surveysInFlight--;
        }
        if (failed) await RescanAsync("incremental update failed");
        else ContinueAfterSurvey(token);
    }

    /// <summary>
    /// 后台核对是拿开始那一刻的存档算的；算的期间你可能收下、退回了成果，雅典娜可能交来新的。合并时账本取核对的结果，
    /// 每件成果的处理状态取现在的，路径 / 指纹 / 遗失标记取核对的——谁也不覆盖谁新近的那一半。
    /// </summary>
    private static PolisSaveDocument MergeReconciled(PolisSaveDocument current, PolisSaveDocument reconciled)
    {
        var byId = reconciled.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var items = current.Items
            .Select(item => byId.TryGetValue(item.Id, out var found)
                ? item with { RelativePath = found.RelativePath, Fingerprint = found.Fingerprint, Lost = found.Lost, ModifiedSinceDelivery = found.ModifiedSinceDelivery }
                : item)
            .ToList();
        // 核对期间新立的户（M-D）写进了此刻的账本：不被核对的结果冲掉
        return current with { Ledger = PolisHouseholds.MergeAfterReconcile(reconciled.Ledger, current.Ledger), Items = items };
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

    // ———————————————————————— 交付与归属 ————————————————————————
    // 订阅的只是"这一回合交付了没有"（气泡停止流式输出）和"她这一回合碰过哪些路径"（工具调用的起止，外部改动的归属要它：
    // 不记的话，她写的每个文件都会被当成外部改动挂上公告板）。工具、推理、审批都不再推给页面。

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null) foreach (ChatMessage message in e.NewItems) WatchMessage(message);
        if (e.OldItems != null) foreach (ChatMessage message in e.OldItems) UnwatchMessage(message);
        MarkDeliveriesDirty();
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
        segment.ToolCalls.CollectionChanged += OnToolCallsChanged;
        foreach (var entry in segment.ToolCalls) entry.PropertyChanged += OnToolCallPropertyChanged;
    }

    private void UnwatchSegment(ChatMessageSegment segment)
    {
        segment.ToolCalls.CollectionChanged -= OnToolCallsChanged;
        foreach (var entry in segment.ToolCalls) entry.PropertyChanged -= OnToolCallPropertyChanged;
    }

    private void OnSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null) foreach (ChatMessageSegment segment in e.NewItems) WatchSegment(segment);
        if (e.OldItems != null) foreach (ChatMessageSegment segment in e.OldItems) UnwatchSegment(segment);
        MarkDeliveriesDirty();
    }

    private void OnToolCallsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null) foreach (ToolCallEntry entry in e.NewItems) entry.PropertyChanged += OnToolCallPropertyChanged;
        if (e.OldItems != null) foreach (ToolCallEntry entry in e.OldItems) entry.PropertyChanged -= OnToolCallPropertyChanged;
        MarkDeliveriesDirty();
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatMessage.IsStreaming) or nameof(ChatMessage.IsLoading) or nameof(ChatMessage.DurationMs))
            MarkDeliveriesDirty();
    }

    private void OnToolCallPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolCallEntry.Status)) MarkDeliveriesDirty();
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConversationSessionItemViewModel.IsWaitingForApproval) || _session == null) return;
        // 等审批（7.1"定"）只在对话栏上显示：红封印、"她在等你点头"。审批本身照旧在原生审批窗口里——网页没有"批准"。
        PostComposer();
    }

    private void MarkDeliveriesDirty()
    {
        _deliveriesDirty = true;
        if (_page != null && !_flushTimer.IsEnabled) _flushTimer.Start();
    }

    /// <summary>交付的那一回合在这里变成一件成果（存进存档，带颜色等你收下）：气泡停止流式输出的那一刻它就出现在城里。</summary>
    private void ObserveDeliveries()
    {
        if (!_deliveriesDirty || _deliveries == null || _session == null) return;
        _deliveriesDirty = false;
        var now = DateTimeOffset.UtcNow;
        foreach (var messageId in _deliveries.Observe(_session.Chat.Messages.ToList(), now)) CreateItem(messageId, now);
    }

    /// <summary>
    /// 这一回合交出来的成果（7.1"收"）：写进工作区的报告是卷轴、表格是账册、图片是彩绘板；没有落成文件的回答本身也是一卷。
    /// 一回合一件（取最后写的那个交付物）。指纹在后台补上。
    /// </summary>
    private PolisItem? CreateItem(string messageId, DateTimeOffset now)
    {
        if (_session == null || _deliveries == null) return null;
        if (_save.Items.Any(i => i.SourceMessageId == messageId)) return null;
        var bubble = _session.Chat.Messages.FirstOrDefault(m => m.Id == messageId);
        if (bubble == null) return null;

        var written = _deliveries.WrittenFiles(bubble)
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
        if (state == PolisItemState.Accepted && !item.Lost)
        {
            // 收下：颜色褪进城邦，摆到它那一类的场馆里第一个空位（7.2）——之后挪去哪、要不要撤下由你决定；满了就先收着
            var placed = PolisCollection.Place(_save, itemId, PolisCollection.DefaultVenue(item.Kind), null);
            if (placed.Ok) _save = placed.Save;
            else AddNotice(F("GameMode.Notice.VenueFull", "{0} is full; {1} is kept in storage for now", VenueName(PolisCollection.DefaultVenue(item.Kind)), item.Title));
        }
        PostItems();
        Flush();
        if (state == PolisItemState.Returned)
        {
            // 退回 = 在同一会话里追加一条修改要求：选中它、预填它自己的输入框（游戏里就是城里的对话栏，展开并聚焦），
            // 由你写完托付（发消息只走一条路径）。按会话 Id 预填：外壳要等第二拍才换中间的会话，预填"当前的"会落到上一个会话里
            if (_shell != null && _shell.SelectedSession?.ConversationId != item.ConversationId) _shell.SelectConversation(item.ConversationId);
            Prefill(item.ConversationId, F("GameMode.Return.Prefill", "About {0}, please change: ", item.Title));
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
        ObserveDeliveries();
        if (_page != null && _talkPartial.Count > 0)
        {
            foreach (var (who, (call, text)) in _talkPartial) _outbox.Add(new { type = "talk", who, n = call, text, done = false });
            _talkPartial.Clear();
        }
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
            _accumulationKey = null;
            RefreshAccumulation(force: true);
            Flush();
        }
    }

    private string L(string key, string fallback) => _localization.GetString(key, fallback);

    /// <summary>场馆的名字（locale 的 Polis.Public.*，与页面上的名牌同一份）。</summary>
    private string VenueName(PolisVenue venue)
    {
        var entry = PolisLocale.PublicNames.First(p => p.JsKey == PolisCollection.Slug(venue));
        return L(entry.Key, entry.Fallback);
    }

    private string F(string key, string fallback, params object[] args)
        => string.Format(CultureInfo.CurrentCulture, L(key, fallback), args);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _askCancel.Cancel();
        _askCancel.Dispose();
        _chorusCancel?.Dispose();
        _chorusCancel = null;
        _debateCancel?.Cancel();
        _debateCancel?.Dispose();
        _debateCancel = null;
        _flushTimer.Stop();
        _readyTimer?.Stop();
        _releaseTimer?.Stop();
        _watcherTimer?.Stop();
        _accumulationTimer?.Stop();
        _relationsSaveTimer?.Stop();
        _cityLoad?.Cancel();
        _cityLoad?.Dispose();
        DetachSession();
        _cronTasks.TasksChanged -= OnCronTasksChanged;
        _watcher.Changed -= OnWorkspaceChanged;
        _watcher.ErrorsDropped -= OnWatcherErrorsDropped;
        _watcher.StateChanged -= OnWatcherStateChanged;
        _localization.LanguageChanged -= OnLanguageChanged;
        _configuration.CurrentChanged -= OnConfigurationReplaced;
        if (_shell != null) _shell.WorkspaceRelocated -= OnWorkspaceRelocated;
    }
}
