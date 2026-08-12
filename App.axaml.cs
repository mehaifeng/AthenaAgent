using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Markup.Xaml;
using Athena.UI.ViewModels;
using Athena.UI.Views;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using Athena.UI.Services.Functions;
using Athena.UI.Services.Preview;
using Athena.UI.Services.SubAgents;
using Athena.UI.Services.Browser;
using Athena.UI.Services.Platform;
using Athena.UI.Services.Skills;
using Athena.UI.Services.ModelMetadata;
using Athena.UI.Services.Context;
using Athena.UI.Services.ConfigSurface;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Athena.UI.Markup;
using Athena.UI.Controls;
using Avalonia.Styling;

namespace Athena.UI;

public partial class App : Application, IAsyncDisposable
{
    /// <summary>
    /// 服务提供者（用于依赖注入）
    /// </summary>
    public static IServiceProvider? Services { get; private set; }

    /// <summary>
    /// 是否正在退出应用程序
    /// </summary>
    public bool IsQuitting { get; private set; }

    /// <summary>
    /// 引导页选择的起手 prompt：主窗口就绪后写入聊天输入框（一次性）。
    /// </summary>
    public static string? PendingStarterPrompt { get; set; }

    /// <summary>
    /// 平台路径服务
    /// </summary>
    private static IPlatformPathService? _platformPathService;

    /// <summary>
    /// 本次启动耗时（进程启动 → 框架初始化完成、主窗口已创建）。
    /// 供 About 页展示启动性能，用户可直接感知原生应用的启动速度。
    /// </summary>
    public static TimeSpan? StartupDuration { get; private set; }

    /// <summary>启动计时起点：App 类型首次加载时记录（≈进程启动瞬间）。</summary>
    private static readonly DateTime StartupTimestamp = DateTime.UtcNow;

    public override void Initialize()
    {
        // Optional parsers and nodes must be registered before XAML creates a renderer.
        MarkdownConfiguration.Configure();

        // 初始化平台路径服务（需要在日志之前初始化）
        _platformPathService = new DesktopPlatformPathService();

        // 初始化 Serilog
        var logDir = _platformPathService.GetLogDirectory();
        var dbPath = Path.Combine(logDir, "logs.db");

        Log.Logger = SerilogConfiguration.CreateLogger(dbPath);
        Log.Information("Application starting... Platform: Desktop");

        AvaloniaXamlLoader.Load(this);
    }

    private void TrayIcon_OnClicked(object? sender, EventArgs e)
    {
        StopTrayFlashing();
        ShowMainWindow();
    }

    private void MenuShow_OnClick(object? sender, EventArgs e)
    {
        StopTrayFlashing();
        ShowMainWindow();
    }

    private void MenuQuit_OnClick(object? sender, EventArgs e)
    {
        StopTrayFlashing();
        IsQuitting = true;
        // 与 macOS Dock/⌘Q 退出共用同一套异步清理流程（保存会话、释放服务、最后 Shutdown）。
        _ = ShutdownAndCleanupAsync();
    }

    private void ShowMainWindow()
    {
        StopTrayFlashing();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            desktop.MainWindow.Show();
            desktop.MainWindow.WindowState = WindowState.Normal;
            desktop.MainWindow.Activate();
        }
    }

    private System.Threading.CancellationTokenSource? _flashCts;
    private bool _disposed;
    private bool _shutdownCleanupInProgress;
    private bool _shutdownCleanupCompleted;

    /// <summary>退出清理的兜底时限：超过后不再等待，直接继续退出流程，避免任何服务卡死阻塞退出。</summary>
    private static readonly TimeSpan ShutdownCleanupTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 开始托盘图标闪烁
    /// </summary>
    public static void StartTrayFlashing()
    {
        if (Current is App app)
        {
            // 检查窗口是否处于活跃状态且可见。如果是，则不闪烁。
            if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
            {
                if (desktop.MainWindow.IsVisible && desktop.MainWindow.IsActive)
                {
                    Log.Debug("Window is in the foreground active state; skipping flash notification");
                    return;
                }
            }
            app.InternalStartFlashing();
        }
    }

    /// <summary>
    /// 停止托盘图标闪烁
    /// </summary>
    public static void StopTrayFlashing()
    {
        if (Current is App app)
        {
            app.InternalStopFlashing();
        }
    }

    private void InternalStartFlashing()
    {
        if (_flashCts != null) return; // 已经在闪烁

        _flashCts = new System.Threading.CancellationTokenSource();
        var token = _flashCts.Token;

        Task.Run(async () =>
        {
            try
            {
                // 在 UI 线程获取托盘图标实例、原始图标和窗口可见性状态
                var (trayIcon, originalIcon) = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync<(TrayIcon?, WindowIcon?)>(() =>
                {
                    var ti = TrayIcon.GetIcons(this)?.FirstOrDefault();
                    return (ti, ti?.Icon);
                });
                if (trayIcon == null || originalIcon == null) return;

                bool isIconVisible = true;

                while (!token.IsCancellationRequested)
                {
                    // 每次切换前在 UI 线程检查窗口是否已激活
                    bool isActive = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        var desktop = this.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
                        var win = desktop?.MainWindow;
                        return win != null && win.IsVisible && win.IsActive;
                    });

                    if (isActive)
                    {
                        Log.Debug("Window activated; stopping tray flashing");
                        break;
                    }

                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        trayIcon.Icon = isIconVisible ? null : originalIcon;
                        isIconVisible = !isIconVisible;
                    });
                    await Task.Delay(500, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Error(ex, "Tray flashing loop encountered an error");
            }
            finally
            {
                // 恢复原始图标并确保可见
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var trayIcon = TrayIcon.GetIcons(this)?.FirstOrDefault();
                    if (trayIcon != null)
                    {
                        if (trayIcon.Icon == null)
                        {
                            try
                            {
                                trayIcon.Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Athena.UI/Assets/Athena.ico")));
                            }
                            catch (Exception ex)
                            {
                                Log.Warning(ex, "Failed to restore tray icon");
                            }
                        }
                        trayIcon.IsVisible = true;
                    }
                });
            }
        }, token);
    }

    private void InternalStopFlashing()
    {
        _flashCts?.Cancel();
        _flashCts?.Dispose();
        _flashCts = null;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Log.Information("Starting framework initialization...");

        // WebView 原生宿主（Office 预览）挂载失败发生在 async-void 调度器延续里，
        // 业务代码无法 try/catch。仅对原生挂载类失败标记已处理并回退二进制占位；
        // 其余未处理异常保持原有崩溃路径（AppDomain.UnhandledException → crash.log）。
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;

        // 配置依赖注入
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        // 桌面平台使用经典桌面生命周期
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 获取配置服务并加载初始主题
            var configService = Services.GetRequiredService<IConfigService>();
            var config = configService.Load();
            var initialTheme = config.Theme;
            SetTheme(initialTheme);
            // 配色方案必须在创建任何窗口之前应用（Applier 只在主窗口 ViewModel 解析后才构造，
            // 首次引导走 OnboardingWindow 时不会触发它）。
            SetColorScheme(config.ColorScheme);

            // 元数据目录先同步发布磁盘 last-known-good/内置 seed；网络刷新后台执行，不阻塞首屏。
            var metadataCatalog = Services.GetService<IOpenRouterModelMetadataCatalog>();
            if (metadataCatalog != null)
            {
                _ = Task.Run(async () =>
                {
                    var result = await metadataCatalog.RefreshAsync(force: false);
                    if (result.Status == Athena.UI.Models.ModelCatalogRefreshStatus.Failed)
                        Log.Warning("OpenRouter metadata background refresh failed; continuing with local catalog: {Message}", result.Message);
                });
            }

            // 更新托盘菜单文本（NativeMenu 不支持 XAML 绑定）
            UpdateTrayMenuText();

            // macOS: 处理 Dock 右键退出
            desktop.ShutdownRequested += OnShutdownRequested;

            // MCP 生命周期：启动时连接已启用服务器并订阅配置变更（后台，不阻塞 UI 首屏）。
            var mcpLifecycle = Services.GetService<Athena.UI.Services.Mcp.McpLifecycleService>();
            if (mcpLifecycle != null)
            {
                _ = Task.Run(async () =>
                {
                    try { await mcpLifecycle.StartAsync(); }
                    catch (Exception ex) { Log.Error(ex, "Failed to start MCP lifecycle"); }
                });
            }

            // 异步服务已在 OnShutdownRequested 中随 DI 容器释放；
            // 最终退出事件只负责等待日志管线冲刷余量。
            desktop.Exit += (_, _) =>
            {
                Log.Information("Application exiting");
                Log.CloseAndFlush();
            };

            if (!config.OnboardingCompleted)
            {
                // 首次启动：先弹引导向导，关窗（完成或跳过）后再创建主窗口。
                // 引导窗口存活期间主窗口尚不存在，须临时切到显式退出模式，
                // 否则引导窗关闭即触发 OnLastWindowClose 退出整个应用。
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var onboarding = new OnboardingWindow(new OnboardingViewModel(
                    configService,
                    Services.GetService<ILocalizationService>(),
                    Services.GetService<IChatService>(),
                    Services.GetService<IModelCatalogService>()));
                desktop.MainWindow = onboarding;

                // 返回引导页当前主题。OnboardingViewModel 只有在最小配置校验通过后才会发起交接。
                // 重新读一次配置：引导页里若切换过主题，initialTheme 已经过期，
                // 直接传下去会让 ShowThemeSplashAsync 把 Avalonia 变体反向回滚，
                // 导致主题按钮/下拉与实际配色失同步。
                var handoffDone = false;
                string GetCurrentOnboardingTheme()
                {
                    return configService.Load().Theme;
                }

                // 正常完成路径（版画揭幕）：
                // 1. 先创建主窗口但不显示——拿到目标尺寸并预置满幕版画；
                // 2. 引导窗中心锚定平缓拉伸到主窗口尺寸，同时版画渐入、满幕定格；
                // 3. 主窗口在引导窗原位以满幕版画开场，之后才关引导窗（屏幕上始终有窗口）；
                // 4. 揭幕（版画渐出）由主窗口 Opened 里的强制闪屏完成。
                onboarding.HandoffRequested = async () =>
                {
                    if (handoffDone) return;
                    handoffDone = true;
                    var theme = GetCurrentOnboardingTheme();

                    var mainWindow = CreateMainWindow(desktop, theme, onboardingHandoff: true);
                    await onboarding.PlayHandoffExitAsync(mainWindow.Width, mainWindow.Height);

                    mainWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                    mainWindow.Position = onboarding.Position;
                    mainWindow.Show();
                    // 部分平台在 Show 后会按启动策略重定位，再钉一次确保与引导窗原位重合
                    mainWindow.Position = onboarding.Position;
                    Log.Information("Main window created (onboarding handoff)");

                    onboarding.Close();
                    desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
                };

                // 标题栏关闭不能绕过最小配置；完成状态只由向导校验成功后写入。
                onboarding.Closed += (_, _) =>
                {
                    if (handoffDone) return;
                    handoffDone = true;
                    var current = configService.Load();
                    if (!current.OnboardingCompleted)
                    {
                        desktop.Shutdown();
                        return;
                    }
                    ShowMainWindow(desktop, current.Theme, onboardingHandoff: true);
                    desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
                };
                onboarding.Show();
                Log.Information("First-launch onboarding window shown");
            }
            else
            {
                ShowMainWindow(desktop, initialTheme);
            }
        }

        base.OnFrameworkInitializationCompleted();

        // 主窗口已创建并显示，记录本次启动耗时（About 页展示）。
        StartupDuration = DateTime.UtcNow - StartupTimestamp;
        Log.Information("Framework initialization completed in {StartupMs}ms",
            StartupDuration.HasValue ? StartupDuration.Value.TotalMilliseconds.ToString("0") : "?");
    }

    private static void OnDispatcherUnhandledException(object? sender, Avalonia.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        if (!OfficePreviewBridge.HandleAttachFailure(e.Exception)) return;
        Log.Warning(e.Exception, "Office preview native host attach failed; falling back to binary placeholder");
        e.Handled = true;
    }

    /// <summary>
    /// 创建并显示主窗口。
    /// </summary>
    private void ShowMainWindow(IClassicDesktopStyleApplicationLifetime desktop, string initialTheme, bool onboardingHandoff = false)
    {
        var mainWindow = CreateMainWindow(desktop, initialTheme, onboardingHandoff);
        mainWindow.Show();
        Log.Information("Main window created");
    }

    /// <summary>
    /// 创建主窗口但不显示（引导交接路径需要先拿到目标尺寸、摆好位置再 Show）。
    /// </summary>
    private MainWindow CreateMainWindow(IClassicDesktopStyleApplicationLifetime desktop, string initialTheme, bool onboardingHandoff = false)
    {
        // 从 DI 容器获取 ViewModel
        var mainViewModel = Services!.GetRequiredService<MainWindowViewModel>();

        // 启动知识库定期整理后台服务（单例惰性创建，须显式解析以启动计时器）
        Services!.GetRequiredService<IKnowledgeBaseMaintenanceService>().Start();

        var mainWindow = new MainWindow
        {
            DataContext = mainViewModel,
        };
        desktop.MainWindow = mainWindow;

        // 引导页选择的起手 prompt：预填聊天输入框（一次性）。
        if (!string.IsNullOrEmpty(PendingStarterPrompt))
        {
            mainViewModel.MainConversationViewModel.InputText = PendingStarterPrompt;
            PendingStarterPrompt = null;
        }

        // 启动动画：等待窗口完全加载后播放。
        // Opened 会在每次从托盘重新 Show() 时再次触发。启动动画只应在
        // 首次启动播放一次：否则引导交接路径会用启动时捕获的旧 initialTheme
        // 调用 ShowThemeSplashAsync，把用户运行期间切换过的主题强行回滚，且
        // 绕过 App.SetTheme 不触发 ThemeChanged，导致按钮图标/配置选中项失同步。
        // （普通启动路径播的入场动画不切换主题，无此风险，但仍按首次保护。）
        var initialSplashShown = false;
        mainWindow.Opened += async (s, e) =>
        {
            if (!initialSplashShown)
            {
                initialSplashShown = true;
                await Task.Delay(100); // 等待UI完全渲染
                if (onboardingHandoff)
                {
                    // 引导交接路径强制播放：即使主题与当前一致也要完成"停留→揭幕"，
                    // 否则覆盖层会永远留在屏幕上/或完全不播导致生硬切换。
                    await mainWindow.ShowThemeSplashAsync(initialTheme, force: true);
                }
                else
                {
                    // 普通启动：播放一次极简"景深聚焦"入场，盖住首帧后
                    // 会话树异步填充的瞬间，让启动过程显得一气呵成。
                    await mainWindow.PlayStartupFocusEntranceAsync();
                }
            }
        };

        // 引导交接：Show() 之前预置满幕版画，首帧即被与引导窗同一张图覆盖
        if (onboardingHandoff)
        {
            mainWindow.PrepareSplashCover(initialTheme);
        }

        return mainWindow;
    }

    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        // macOS Dock 右键退出时，确保真正退出
        IsQuitting = true;

        // 首次关闭请求先暂停退出，异步释放 DI 容器内的 Browser/MCP 等服务。
        // 释放完成后的第二次 Shutdown 会直接放行并触发 Exit。
        if (_shutdownCleanupCompleted) return;
        e.Cancel = true;
        if (_shutdownCleanupInProgress) return;

        await ShutdownAndCleanupAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _flashCts?.Cancel();
        _flashCts?.Dispose();
        _flashCts = null;

        var services = Services;
        Services = null;
        if (services is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (services is IDisposable disposable)
            disposable.Dispose();
    }

    private async Task PersistSessionStateAsync()
    {
        try
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow?.DataContext is MainWindowViewModel viewModel)
            {
                await viewModel.PersistSessionStateAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save main conversation session state");
        }
    }

    /// <summary>
    /// 统一的退出清理流程：保存会话状态、释放主窗口 ViewModel 与 DI 容器，最后真正关闭应用。
    /// 全程带限时兜底——任何服务卡死（如 MCP/浏览器/PTY 释放）都不会阻止应用退出。
    /// 必须在 UI 线程上调用（OnShutdownRequested / 托盘退出均满足）。
    /// </summary>
    private async Task ShutdownAndCleanupAsync()
    {
        if (_shutdownCleanupInProgress || _shutdownCleanupCompleted) return;
        _shutdownCleanupInProgress = true;
        try
        {
            var cleanup = CleanupAsync();
            var winner = await Task.WhenAny(cleanup, Task.Delay(ShutdownCleanupTimeout));
            if (winner != cleanup)
            {
                Log.Warning(
                    "Shutdown cleanup exceeded {TimeoutSeconds} seconds; forcing exit to continue",
                    ShutdownCleanupTimeout.TotalSeconds);
                _ = cleanup.ContinueWith(
                    static (task, _) =>
                    {
                        if (task.IsFaulted)
                            Log.Error(task.Exception, "Background shutdown task finally failed");
                    },
                    System.Threading.Tasks.TaskScheduler.Default);
            }
            else
            {
                try
                {
                    await cleanup;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Shutdown cleanup failed");
                }
            }
        }
        finally
        {
            _shutdownCleanupInProgress = false;
            _shutdownCleanupCompleted = true;
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }
    }

    private async Task CleanupAsync()
    {
        try
        {
            await PersistSessionStateAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save main conversation session state");
        }

        try
        {
            if (Services?.GetService(typeof(MainWindowViewModel)) is MainWindowViewModel viewModel)
                viewModel.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to dispose main window ViewModel");
        }

        try
        {
            await DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to dispose application services");
        }
    }

    /// <summary>
    /// 更新托盘菜单文本（支持多语言）
    /// </summary>
    private void UpdateTrayMenuText()
    {
        try
        {
            var localizationService = Services?.GetService<ILocalizationService>();
            if (localizationService == null) return;

            var trayIcons = TrayIcon.GetIcons(this);
            var trayIcon = trayIcons?.FirstOrDefault();
            if (trayIcon?.Menu?.Items is { } items)
            {
                if (items.Count > 0 && items[0] is NativeMenuItem showItem)
                    showItem.Header = localizationService.GetString("Tray.Show", "显示主窗口");
                if (items.Count > 1 && items[1] is NativeMenuItem quitItem)
                    quitItem.Header = localizationService.GetString("Tray.Quit", "退出");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to update tray menu text");
        }
    }

    /// <summary>
    /// 主题变更事件广播，供各 ViewModel 同步状态用
    /// </summary>
    public static event Action<string>? ThemeChanged;

    /// <summary>
    /// 配色方案变更事件广播（明暗切换走 ThemeChanged；配色方案切换走本事件）。
    /// </summary>
    public static event Action<string>? ColorSchemeChanged;

    /// <summary>内置方案 + 支持的风格方案（大小写不敏感匹配）。</summary>
    private static readonly string[] KnownColorSchemes = ["Default", "Solarized", "Cyberpunk", "Tokyo", "Monokai"];

    /// <summary>当前生效的配色方案名（App.axaml 内置主题字典即 "Default" 方案）。</summary>
    private static string _currentColorScheme = "Default";

    /// <summary>App.axaml 内置主题字典实例缓存，切回 Default 方案时还原。</summary>
    private static ResourceDictionary? _defaultDarkDictionary;
    private static ResourceDictionary? _defaultLightDictionary;

    /// <summary>已加载方案的字典实例缓存（SetColorScheme 与缩略图控件共用同一实例）。</summary>
    private static readonly Dictionary<string, (ResourceDictionary Dark, ResourceDictionary Light)> _schemeDictionaries = new();

    /// <summary>当前生效的配色方案名。</summary>
    public static string CurrentColorScheme => _currentColorScheme;

    /// <summary>归一化方案名：大小写不敏感匹配已知方案，未命中回退 "Default"。</summary>
    public static string NormalizeColorScheme(string? name)
    {
        foreach (var candidate in KnownColorSchemes)
        {
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return "Default";
    }

    /// <summary>
    /// 获取方案的明暗字典实例；"Default" 返回 App.axaml 内置字典缓存实例。
    /// 供 SetColorScheme 与 ColorSchemeThumbnailView 共用同一份实例缓存。
    /// </summary>
    public static (ResourceDictionary Dark, ResourceDictionary Light) GetColorSchemeDictionaries(string schemeName)
    {
        var scheme = NormalizeColorScheme(schemeName);
        _defaultDarkDictionary ??= GetBuiltInThemeDictionary(ThemeVariant.Dark);
        _defaultLightDictionary ??= GetBuiltInThemeDictionary(ThemeVariant.Light);
        if (scheme == "Default")
            return (_defaultDarkDictionary, _defaultLightDictionary);
        if (_schemeDictionaries.TryGetValue(scheme, out var cached))
            return cached;
        var dark = LoadSchemeDictionary(scheme, "Dark");
        var light = LoadSchemeDictionary(scheme, "Light");
        var pair = (dark, light);
        _schemeDictionaries[scheme] = pair;
        return pair;
    }

    private static ResourceDictionary GetBuiltInThemeDictionary(ThemeVariant variant)
    {
        if (Current?.Resources.ThemeDictionaries is { } themeDictionaries
            && themeDictionaries.TryGetValue(variant, out var provider)
            && provider is ResourceDictionary dict)
        {
            return dict;
        }
        throw new InvalidOperationException($"内置主题字典 {variant.Key} 未就绪");
    }

    private static ResourceDictionary LoadSchemeDictionary(string scheme, string mode) =>
        (ResourceDictionary)AvaloniaXamlLoader.Load(
            new Uri($"avares://Athena.UI/Styles/Themes/{scheme}.{mode}.axaml"));

    /// <summary>
    /// 全局设置配色方案：整体替换 ThemeDictionaries 的 Dark/Light 条目，所有 DynamicResource 消费者即时重绘。
    /// 幂等（与当前方案相同则无操作）；加载失败保持当前方案。须在 UI 线程调用。
    /// </summary>
    /// <param name="schemeName">Default / Solarized / Cyberpunk / Tokyo / Monokai（大小写不敏感）</param>
    public static void SetColorScheme(string schemeName)
    {
        if (Current is not App app) return;
        var scheme = NormalizeColorScheme(schemeName);
        if (string.Equals(scheme, _currentColorScheme, StringComparison.Ordinal)) return;

        ResourceDictionary dark, light;
        try
        {
            (dark, light) = GetColorSchemeDictionaries(scheme);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load color scheme {Scheme}; keeping current scheme", scheme);
            return;
        }

        app.Resources.ThemeDictionaries[ThemeVariant.Dark] = dark;
        app.Resources.ThemeDictionaries[ThemeVariant.Light] = light;
        _currentColorScheme = scheme;
        Log.Information("Color scheme switched to: {Scheme}", scheme);
        ColorSchemeChanged?.Invoke(scheme);
    }

    /// <summary>
    /// 全局设置主题
    /// </summary>
    /// <param name="themeName">"Dark" 或 "Light"</param>
    public static async void SetTheme(string themeName)
    {
        if (Current == null) return;

        var isDark = themeName?.ToLower() != "light";
        var theme = isDark ? ThemeVariant.Dark : ThemeVariant.Light;

        // 触发主题过渡动画（运行期为背景"景深聚焦"过渡；引导交接为满幕版画揭幕）
        if (Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow is Views.MainWindow mainWindow)
        {
            await mainWindow.ShowThemeSplashAsync(themeName ?? "Dark");
        }
        else
        {
            // 非桌面环境或窗口未就绪时直接切换
            Current.RequestedThemeVariant = theme;
        }
        Log.Information("Theme switched to: {Theme}", theme);

        // 广播给所有订阅者（配置会话 / 对话视图模型同步按钮状态）
        ThemeChanged?.Invoke(themeName ?? "Dark");
    }

    /// <summary>
    /// 配置服务依赖注入
    /// </summary>
    private static void ConfigureServices(IServiceCollection services)
    {
        Log.Debug("Configuring dependency injection services...");

        // 平台路径服务（单例）
        services.AddSingleton<IPlatformPathService>(_platformPathService!);

        // 本地化服务（单例）
        services.AddSingleton<ILocalizationService, LocalizationService>();

        // 重复规则服务（单例）
        services.AddSingleton<IRecurrenceService>(sp =>
        {
            var localizationService = sp.GetRequiredService<ILocalizationService>();
            var logger = Log.ForContext<RecurrenceService>();
            return new RecurrenceService(localizationService, logger);
        });

        // 日志服务（单例）
        services.AddSingleton<ILogService, LogService>();

        // CLI 服务（单例）
        services.AddSingleton<ICliService, CliService>();
        services.AddSingleton<ITerminalSessionManager>(sp =>
            new TerminalSessionManager(Log.ForContext<TerminalSessionManager>()));
        services.AddSingleton<TerminalPanelViewModel>();
        services.AddSingleton<ISystemAudioService>(sp =>
        {
            var cliService = sp.GetRequiredService<ICliService>();
            var logger = Log.ForContext<SystemAudioService>();
            return new SystemAudioService(cliService, logger);
        });
        services.AddSingleton<IScreenCaptureService>(sp =>
        {
            var cliService = sp.GetRequiredService<ICliService>();
            var logger = Log.ForContext<ScreenCaptureService>();
            return new ScreenCaptureService(cliService, logger);
        });

        // 配置服务（单例）
        services.AddSingleton<IConfigService, ConfigService>();
        services.AddSingleton<AppConfigurationSession>();
        services.AddSingleton<AppConfigurationApplier>();
        services.AddSingleton<IPetDexCatalogService>(sp =>
            new PetDexCatalogService(
                sp.GetRequiredService<IPlatformPathService>(),
                new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
                Log.ForContext<PetDexCatalogService>()));
        services.AddSingleton<ConversationExecutionCoordinator>();
        services.AddSingleton<ChatSessionFactory>();
        services.AddSingleton<WorkspaceOperationCoordinator>();
        services.AddSingleton<ICommitMessageGenerator>(sp =>
            new CommitMessageGenerator(
                sp.GetRequiredService<OpenAiModelRuntimeFactory>(),
                sp.GetRequiredService<IPromptService>(),
                Log.ForContext<CommitMessageGenerator>()));
        services.AddSingleton<OfficePreviewHost>();
        services.AddSingleton<WorkspaceWorkbenchViewModel>(sp =>
            new WorkspaceWorkbenchViewModel(
                sp.GetRequiredService<WorkspaceOperationCoordinator>(),
                sp.GetRequiredService<IPlatformPathService>(),
                sp.GetRequiredService<IUserInteractionService>(),
                sp.GetService<ICommitMessageGenerator>(),
                sp.GetService<ILocalizationService>(),
                sp.GetService<OfficePreviewHost>()));
        services.AddSingleton<AboutViewModel>();
        services.AddTransient<AppSettingsWindowViewModel>();
        services.AddTransient<ProviderModelsViewModel>();
        services.AddTransient<SkillsViewModel>(sp =>
        {
            var viewModel = new SkillsViewModel(
                sp.GetService<ISkillCatalogService>(),
                sp.GetService<IConfigService>(),
                sp.GetService<IWorkspaceService>(),
                sp.GetService<ILocalizationService>(),
                sp.GetService<IUserInteractionService>());
            viewModel.Initialize(sp.GetRequiredService<AppConfigurationSession>());
            return viewModel;
        });
        services.AddTransient<McpConnectionsViewModel>(sp =>
        {
            var viewModel = new McpConnectionsViewModel(
                sp.GetService<IConfigService>(),
                sp.GetService<ILocalizationService>());
            viewModel.Initialize(sp.GetRequiredService<AppConfigurationSession>());
            return viewModel;
        });
        services.AddTransient<SpeechSettingsViewModel>();
        services.AddTransient<ImageGenerationSettingsViewModel>();
        services.AddTransient<WebSearchSettingsViewModel>();
        services.AddTransient<DocumentParserSettingsViewModel>();
        services.AddTransient<SkillsConnectorsWindowViewModel>();

        // Token 统计服务（单例，跨页面同步）
        services.AddSingleton<ITokenService, TokenService>();

        // 系统文件服务（单例）
        services.AddSingleton<IFileSystemService>(sp =>
        {
            var configService = sp.GetRequiredService<IConfigService>();
            var pathService = sp.GetRequiredService<IPlatformPathService>();
            var logger = Log.ForContext<FileSystemService>();
            return new FileSystemService(configService, pathService, logger);
        });

        // 任务调度器（单例，UI 和 Function Calling 共享）
        services.AddSingleton<ITaskScheduler>(sp =>
        {
            var logger = Log.ForContext<Services.TaskScheduler>();
            var pathService = sp.GetRequiredService<IPlatformPathService>();
            var recurrenceService = sp.GetRequiredService<IRecurrenceService>();
            var scheduler = new Services.TaskScheduler(logger, pathService, recurrenceService);
            scheduler.Start(); // 启动调度器
            Log.Information("Task scheduler started");
            return scheduler;
        });

        // Embedding 服务（单例，用于向量语义检索）
        services.AddSingleton<IEmbeddingService>(sp =>
        {
            var configService = sp.GetRequiredService<IConfigService>();
            var config = configService.Load();
            var logger = Log.ForContext<OpenAIEmbeddingService>();
            var localizationService = sp.GetService<ILocalizationService>();
            try
            {
                var embedding = OpenAiModelRuntimeFactory.Resolve(config, Athena.UI.Models.AiModelRole.Embedding);
                Log.Information(
                    "Embedding service initialized, provider: {Provider}, model: {Model}",
                    embedding.ProviderDisplayName,
                    embedding.Model);
            }
            catch (InvalidOperationException)
            {
                Log.Information("Embedding service initialized without a configured provider/model");
            }
            return new OpenAIEmbeddingService(config, logger, localizationService);
        });

        // 知识库服务（单例）
        services.AddSingleton<IKnowledgeBaseService>(sp =>
        {
            var logger = Log.ForContext<KnowledgeBaseService>();
            var embeddingService = sp.GetService<IEmbeddingService>();
            var platformPathService = sp.GetRequiredService<IPlatformPathService>();
            var service = new KnowledgeBaseService(logger, embeddingService, platformPathService);

            // 异步初始化（加载向量缓存）
            _ = Task.Run(async () =>
            {
                try
                {
                    await service.InitializeAsync();
                    Log.Information("Knowledge base service initialized");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to initialize knowledge base service");
                }
            });

            return service;
        });

        // Function 相关类（使用工厂方法提供 Logger）
        services.AddSingleton<ProactiveMessagingFunctions>(sp =>
        {
            var taskScheduler = sp.GetRequiredService<ITaskScheduler>();
            var recurrenceService = sp.GetRequiredService<IRecurrenceService>();
            var logger = Log.ForContext<ProactiveMessagingFunctions>();
            return new ProactiveMessagingFunctions(taskScheduler, recurrenceService, logger);
        });

        services.AddSingleton<KnowledgeBaseFunctions>(sp =>
        {
            var knowledgeBase = sp.GetRequiredService<IKnowledgeBaseService>();
            var logger = Log.ForContext<KnowledgeBaseFunctions>();
            return new KnowledgeBaseFunctions(knowledgeBase, logger);
        });

        services.AddSingleton<ConfigurationFunctions>(sp =>
        {
            var configService = sp.GetRequiredService<IConfigService>();
            var configSurface = sp.GetRequiredService<IConfigSurfaceService>();
            var logger = Log.ForContext<ConfigurationFunctions>();
            return new ConfigurationFunctions(configService, configSurface, sp, logger);
        });

        services.AddSingleton<FileSystemFunctions>(sp =>
        {
            var fileSystemService = sp.GetRequiredService<IFileSystemService>();
            var knowledgeBaseService = sp.GetRequiredService<IKnowledgeBaseService>();
            var workspaceService = sp.GetService<IWorkspaceService>();
            var documentParserService = sp.GetService<IDocumentParserService>();
            var logger = Log.ForContext<FileSystemFunctions>();
            return new FileSystemFunctions(fileSystemService, knowledgeBaseService, logger, workspaceService, documentParserService);
        });

        services.AddSingleton<CliFunctions>(sp =>
        {
            var cliService = sp.GetRequiredService<ICliService>();
            var configService = sp.GetRequiredService<IConfigService>();
            var logger = Log.ForContext<CliFunctions>();
            return new CliFunctions(cliService, configService, logger);
        });

        // Web Search 服务
        services.AddSingleton<IWebSearchService>(sp =>
        {
            var configService = sp.GetRequiredService<IConfigService>();
            var logger = Log.ForContext<WebSearchService>();
            var localizationService = sp.GetService<ILocalizationService>();
            return new WebSearchService(configService, logger, localizationService);
        });

        services.AddSingleton<IUpdateService>(sp =>
        {
            var logger = Log.ForContext<GitHubUpdateService>();
            return new GitHubUpdateService(logger);
        });

        services.AddSingleton<IAttachmentStoreService>(sp =>
        {
            var pathService = sp.GetRequiredService<IPlatformPathService>();
            var logger = Log.ForContext<AttachmentStoreService>();
            return new AttachmentStoreService(pathService, logger);
        });

        services.AddSingleton<IDocumentParserService>(sp =>
        {
            var configService = sp.GetRequiredService<IConfigService>();
            var logger = Log.ForContext<Services.Parsers.MinerUDocumentParserService>();
            return new Services.Parsers.MinerUDocumentParserService(configService, logger);
        });

        services.AddSingleton<IConversationSessionAccessor, ConversationSessionAccessor>();
        services.AddSingleton<IUserInteractionService, AvaloniaUserInteractionService>();

        services.AddSingleton<ConversationArchiveStore>(sp =>
            new ConversationArchiveStore(
                sp.GetRequiredService<IPlatformPathService>(),
                Log.ForContext<ConversationArchiveStore>()));
        services.AddSingleton<IConversationArchiveStore>(sp => sp.GetRequiredService<ConversationArchiveStore>());
        services.AddSingleton<IConversationDraftStore>(sp => sp.GetRequiredService<ConversationArchiveStore>());
        services.AddSingleton<IConversationTitleGenerator>(sp =>
            new ConversationTitleGenerator(
                sp.GetRequiredService<OpenAiModelRuntimeFactory>(),
                sp.GetRequiredService<IPromptService>(),
                Log.ForContext<ConversationTitleGenerator>(),
                sp.GetService<ILocalizationService>()));
        services.AddSingleton<IWorkspaceKnowledgeCompressor>(sp =>
            new WorkspaceKnowledgeCompressor(
                sp.GetRequiredService<OpenAiModelRuntimeFactory>(),
                Log.ForContext<WorkspaceKnowledgeCompressor>()));

        services.AddSingleton<IWorkspaceService>(sp =>
        {
            var platformPathService = sp.GetRequiredService<IPlatformPathService>();
            var configService = sp.GetService<IConfigService>();
            var knowledgeCompressor = sp.GetService<IWorkspaceKnowledgeCompressor>();
            var logger = Log.ForContext<WorkspaceService>();
            return new WorkspaceService(platformPathService, logger, configService, knowledgeCompressor);
        });

        services.AddSingleton<IImageGenerationSessionService>(sp =>
        {
            var pathService = sp.GetRequiredService<IPlatformPathService>();
            var logger = Log.ForContext<ImageGenerationSessionService>();
            return new ImageGenerationSessionService(pathService, logger);
        });

        services.AddSingleton<IImageGenerationService>(sp =>
        {
            var configService = sp.GetRequiredService<IConfigService>();
            var attachmentStoreService = sp.GetRequiredService<IAttachmentStoreService>();
            var logger = Log.ForContext<OpenAIImageGenerationService>();
            return new OpenAIImageGenerationService(configService, attachmentStoreService, logger);
        });

        // Headless Browser 服务
        services.AddSingleton<IBrowserSessionManager>(sp =>
        {
            var logger = Log.ForContext<BrowserSessionManager>();
            return new BrowserSessionManager(logger);
        });

        services.AddSingleton<ISomAnnotator>(sp =>
        {
            var pathService = sp.GetRequiredService<IPlatformPathService>();
            var logger = Log.ForContext<SomAnnotator>();
            return new SomAnnotator(pathService, logger);
        });

        services.AddSingleton<IHeadlessBrowserService>(sp =>
        {
            var sessionManager = sp.GetRequiredService<IBrowserSessionManager>();
            var somAnnotator = sp.GetRequiredService<ISomAnnotator>();
            var logger = Log.ForContext<PlaywrightBrowserService>();
            return new PlaywrightBrowserService(sessionManager, somAnnotator, logger);
        });

        services.AddSingleton<IBrowserVisionService>(sp =>
        {
            var configService = sp.GetRequiredService<IConfigService>();
            var logger = Log.ForContext<BrowserVisionService>();
            var localizationService = sp.GetService<ILocalizationService>();
            return new BrowserVisionService(configService, logger, localizationService);
        });

        services.AddSingleton<IBrowserTaskPlanner>(sp =>
        {
            var configService = sp.GetRequiredService<IConfigService>();
            var logger = Log.ForContext<BrowserTaskPlanner>();
            return new BrowserTaskPlanner(configService, logger);
        });

        services.AddSingleton<IBrowserAgentService>(sp =>
        {
            var browserService = sp.GetRequiredService<IHeadlessBrowserService>();
            var browserVisionService = sp.GetRequiredService<IBrowserVisionService>();
            var taskPlanner = sp.GetRequiredService<IBrowserTaskPlanner>();
            var configService = sp.GetRequiredService<IConfigService>();
            var logger = Log.ForContext<BrowserAgentService>();
            return new BrowserAgentService(browserService, browserVisionService, taskPlanner, configService, logger);
        });

        services.AddSingleton<WebSearchFunctions>(sp =>
        {
            var webSearchService = sp.GetRequiredService<IWebSearchService>();
            var logger = Log.ForContext<WebSearchFunctions>();
            return new WebSearchFunctions(webSearchService, logger);
        });

        services.AddSingleton<ImageGenerationFunctions>(sp =>
        {
            var imageGenerationService = sp.GetRequiredService<IImageGenerationService>();
            var imageGenerationSessionService = sp.GetRequiredService<IImageGenerationSessionService>();
            var conversationSessionAccessor = sp.GetRequiredService<IConversationSessionAccessor>();
            var logger = Log.ForContext<ImageGenerationFunctions>();
            return new ImageGenerationFunctions(imageGenerationService, imageGenerationSessionService, conversationSessionAccessor, logger);
        });

        services.AddSingleton<BrowserTaskFunctions>(sp =>
        {
            var browserAgentService = sp.GetRequiredService<IBrowserAgentService>();
            var logger = Log.ForContext<BrowserTaskFunctions>();
            return new BrowserTaskFunctions(browserAgentService, logger);
        });

        // 子代理编排器（单例）。惰性解析 IFunctionRegistry（经 IServiceProvider）以断开构造环。
        services.AddSingleton<ISubAgentOrchestrator>(sp =>
        {
            var configService = sp.GetRequiredService<IConfigService>();
            var logger = Log.ForContext<SubAgentOrchestrator>();
            return new SubAgentOrchestrator(configService, sp, logger);
        });

        services.AddSingleton<SubAgentFunctions>(sp =>
        {
            var orchestrator = sp.GetRequiredService<ISubAgentOrchestrator>();
            var logger = Log.ForContext<SubAgentFunctions>();
            return new SubAgentFunctions(orchestrator, logger);
        });

        services.AddSingleton<DocumentParserFunctions>(sp =>
        {
            var documentParserService = sp.GetRequiredService<IDocumentParserService>();
            var fileSystemService = sp.GetRequiredService<IFileSystemService>();
            var logger = Log.ForContext<DocumentParserFunctions>();
            return new DocumentParserFunctions(documentParserService, fileSystemService, logger);
        });

        services.AddSingleton<Athena.UI.Services.Spreadsheets.XlsxPackageService>();
        services.AddSingleton<SpreadsheetFunctions>(sp =>
            new SpreadsheetFunctions(
                sp.GetRequiredService<IFileSystemService>(),
                sp.GetRequiredService<Athena.UI.Services.Spreadsheets.XlsxPackageService>(),
                Log.ForContext<SpreadsheetFunctions>()));

        // Function Registry（单例）
        // 工具审批弹窗展示器（UI 层）。服务层只依赖接口。
        services.AddSingleton<ApprovalQueueViewModel>(sp =>
            new ApprovalQueueViewModel(
                sp.GetService<IConversationSessionAccessor>(),
                Log.ForContext<ApprovalQueueViewModel>(),
                sp.GetService<ILocalizationService>()));
        services.AddSingleton<IToolApprovalPrompter>(sp => sp.GetRequiredService<ApprovalQueueViewModel>());

        // 工具审批服务（策略大脑 + 审计）。被 FunctionRegistry 这个唯一 chokepoint 调用。
        services.AddSingleton<IAiToolApprovalEvaluator>(sp =>
            new AiToolApprovalEvaluator(
                sp.GetRequiredService<OpenAiModelRuntimeFactory>(),
                Log.ForContext<AiToolApprovalEvaluator>()));

        services.AddSingleton<IToolApprovalService>(sp =>
        {
            var configService = sp.GetRequiredService<IConfigService>();
            var prompter = sp.GetService<IToolApprovalPrompter>();
            var sessionAccessor = sp.GetService<IConversationSessionAccessor>();
            var aiEvaluator = sp.GetService<IAiToolApprovalEvaluator>();
            var localizationService = sp.GetService<ILocalizationService>();
            return new ToolApprovalService(configService, prompter, Log.ForContext<ToolApprovalService>(), sessionAccessor, aiEvaluator, localizationService, sp.GetRequiredService<ConversationExecutionCoordinator>());
        });

        // --- MCP 扩展（Model Context Protocol）---
        // Registry 是工具索引；ClientManager 兼任 IMcpToolHost（拉起 stdio 子进程 + 提供只读快照）；
        // DiscoveryFunctions 是暴露给主模型的三枚 meta-tool 的实现。
        services.AddSingleton<Athena.UI.Services.Mcp.McpToolRegistry>();
        services.AddSingleton<Athena.UI.Services.Mcp.McpClientManager>(sp =>
            new Athena.UI.Services.Mcp.McpClientManager(
                sp.GetRequiredService<Athena.UI.Services.Mcp.McpToolRegistry>(),
                Log.ForContext<Athena.UI.Services.Mcp.McpClientManager>()));
        services.AddSingleton<Athena.UI.Services.Mcp.IMcpToolHost>(sp =>
            sp.GetRequiredService<Athena.UI.Services.Mcp.McpClientManager>());
        services.AddSingleton<Athena.UI.Services.Mcp.IMcpServerController>(sp =>
            sp.GetRequiredService<Athena.UI.Services.Mcp.McpClientManager>());
        services.AddSingleton<Athena.UI.Services.Mcp.McpDiscoveryFunctions>(sp =>
            new Athena.UI.Services.Mcp.McpDiscoveryFunctions(
                sp.GetRequiredService<Athena.UI.Services.Mcp.IMcpToolHost>(),
                Log.ForContext<Athena.UI.Services.Mcp.McpDiscoveryFunctions>()));
        // 服务器管理工具：经审批弹窗新增/移除 MCP 服务器；保存配置即触发热重启。
        services.AddSingleton<Athena.UI.Services.Mcp.McpManagementFunctions>(sp =>
            new Athena.UI.Services.Mcp.McpManagementFunctions(
                sp.GetRequiredService<IConfigService>(),
                Log.ForContext<Athena.UI.Services.Mcp.McpManagementFunctions>()));
        // 生命周期服务：启动时按配置连接、配置变更热重启、退出时释放子进程。
        services.AddSingleton<Athena.UI.Services.Mcp.McpLifecycleService>(sp =>
            new Athena.UI.Services.Mcp.McpLifecycleService(
                sp.GetRequiredService<Athena.UI.Services.Mcp.IMcpServerController>(),
                sp.GetRequiredService<IConfigService>(),
                Log.ForContext<Athena.UI.Services.Mcp.McpLifecycleService>()));

        // --- Agent Skills ---
        services.AddSingleton<ISkillCatalogService>(sp =>
            new SkillCatalogService(
                sp.GetRequiredService<IPlatformPathService>(),
                sp.GetRequiredService<IConfigService>(),
                Log.ForContext<SkillCatalogService>(),
                sp.GetService<IWorkspaceService>()));
        services.AddSingleton<SkillFunctions>(sp =>
            new SkillFunctions(
                sp.GetRequiredService<ISkillCatalogService>(),
                Log.ForContext<SkillFunctions>(),
                sp.GetService<IConversationSessionAccessor>()));

        services.AddSingleton<IFunctionRegistry>(sp =>
        {
            var proactiveFunctions = sp.GetRequiredService<ProactiveMessagingFunctions>();
            var knowledgeFunctions = sp.GetRequiredService<KnowledgeBaseFunctions>();
            var configFunctions = sp.GetRequiredService<ConfigurationFunctions>();
            var fileSystemFunctions = sp.GetRequiredService<FileSystemFunctions>();
            var cliFunctions = sp.GetRequiredService<CliFunctions>();
            var webSearchFunctions = sp.GetRequiredService<WebSearchFunctions>();
            var imageGenerationFunctions = sp.GetRequiredService<ImageGenerationFunctions>();
            var browserTaskFunctions = sp.GetRequiredService<BrowserTaskFunctions>();
            var subAgentFunctions = sp.GetRequiredService<SubAgentFunctions>();
            var documentParserFunctions = sp.GetRequiredService<DocumentParserFunctions>();
            var spreadsheetFunctions = sp.GetRequiredService<SpreadsheetFunctions>();
            var configService = sp.GetService<IConfigService>();
            var approvalService = sp.GetService<IToolApprovalService>();
            var mcpDiscoveryFunctions = sp.GetService<Athena.UI.Services.Mcp.McpDiscoveryFunctions>();
            var mcpManagementFunctions = sp.GetService<Athena.UI.Services.Mcp.McpManagementFunctions>();
            var skillFunctions = sp.GetService<SkillFunctions>();
            var logger = Log.ForContext<FunctionRegistry>();

            return new FunctionRegistry(proactiveFunctions, knowledgeFunctions, configFunctions, fileSystemFunctions, cliFunctions, webSearchFunctions, imageGenerationFunctions, browserTaskFunctions, subAgentFunctions, documentParserFunctions, spreadsheetFunctions, configService, logger, approvalService, mcpDiscoveryFunctions, mcpManagementFunctions, skillFunctions);
        });

        // 知识库整理 headless Agent 运行器（惰性解析 IFunctionRegistry 以断开构造环）
        services.AddSingleton<KnowledgeBaseMaintenanceRunner>(sp =>
        {
            var configService = sp.GetRequiredService<IConfigService>();
            var functionRegistry = sp.GetRequiredService<IFunctionRegistry>();
            var logger = Log.ForContext<KnowledgeBaseMaintenanceRunner>();
            var localizationService = sp.GetService<ILocalizationService>();
            return new KnowledgeBaseMaintenanceRunner(configService, functionRegistry, logger, localizationService);
        });

        // 知识库定期整理服务（单例，后台计时器）
        services.AddSingleton<IKnowledgeBaseMaintenanceService>(sp =>
        {
            var configService = sp.GetRequiredService<IConfigService>();
            var knowledgeBase = sp.GetRequiredService<IKnowledgeBaseService>();
            var embeddingService = sp.GetRequiredService<IEmbeddingService>();
            var pathService = sp.GetRequiredService<IPlatformPathService>();
            var runner = sp.GetRequiredService<KnowledgeBaseMaintenanceRunner>();
            var logger = Log.ForContext<KnowledgeBaseMaintenanceService>();
            return new KnowledgeBaseMaintenanceService(configService, knowledgeBase, embeddingService, pathService, runner, logger);
        });

        // Prompt 服务（单例）
        services.AddSingleton<IPromptService, PromptService>();

        // 模型列表查询服务（无状态，按需用各字段的 BaseUrl/Key 临时构造客户端）
        services.AddSingleton<IModelCatalogService, ModelCatalogService>();
        services.AddSingleton<ModelIdentityMatcher>();
        services.AddSingleton<ModelMetadataResolver>();
        services.AddSingleton<IModelMetadataResolver>(sp => sp.GetRequiredService<ModelMetadataResolver>());
        services.AddSingleton<IConfigSurfaceService>(sp =>
            new ConfigSurfaceService(sp.GetService<IOpenRouterModelMetadataCatalog>()));
        services.AddSingleton<ModelContextPolicyResolver>();
        services.AddSingleton<IModelContextPolicyResolver>(sp => sp.GetRequiredService<ModelContextPolicyResolver>());
        services.AddSingleton<IProviderErrorClassifier, ProviderErrorClassifier>();
        services.AddSingleton<OpenRouterModelMetadataStore>(sp =>
            new OpenRouterModelMetadataStore(
                sp.GetRequiredService<IPlatformPathService>(),
                Log.ForContext<OpenRouterModelMetadataStore>()));
        services.AddSingleton<IOpenRouterModelMetadataCatalog>(sp =>
        {
            var configService = sp.GetRequiredService<IConfigService>();
            return new OpenRouterModelMetadataCatalog(
                new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
                sp.GetRequiredService<OpenRouterModelMetadataStore>(),
                OpenRouterSeedCatalog.Load(),
                Log.ForContext<OpenRouterModelMetadataCatalog>(),
                apiKeyProvider: () => configService.Load().AiModels.Providers
                    .FirstOrDefault(provider =>
                        Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri)
                        && (uri.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase)
                            || uri.Host.EndsWith(".openrouter.ai", StringComparison.OrdinalIgnoreCase)))
                    ?.ApiKey);
        });
        services.AddSingleton<IContextPolicyProvider, ContextPolicyProvider>();
        services.AddSingleton<TokenFingerprintService>();
        services.AddSingleton<IContextRequestPreparer, ContextRequestPreparer>();
        services.AddSingleton<ICompressionPlanner, CompressionPlanner>();
        services.AddSingleton<ICompressionTextGenerator, OpenAiCompressionTextGenerator>();
        services.AddSingleton<ICompressionCandidateGenerator>(sp =>
            new CompressionCandidateGenerator(
                sp.GetRequiredService<ICompressionTextGenerator>(),
                sp.GetRequiredService<IPromptService>(),
                Log.ForContext<CompressionCandidateGenerator>()));
        services.AddSingleton<ICompressionValidator, CompressionValidator>();
        services.AddSingleton<ITokenCalibrationService>(sp =>
            new TokenCalibrationService(
                sp.GetRequiredService<IPlatformPathService>(),
                sp.GetRequiredService<TokenFingerprintService>(),
                Log.ForContext<TokenCalibrationService>()));

        services.AddSingleton<OpenAiModelRuntimeFactory>();
        // AI 对话服务（单例，共享配置）
        services.AddSingleton<IChatService>(sp =>
        {
            var configService = sp.GetRequiredService<IConfigService>();
            var promptService = sp.GetRequiredService<IPromptService>();
            var locationService = sp.GetService<ILocalizationService>();
            var attachmentStoreService = sp.GetService<IAttachmentStoreService>();
            var conversationSessionAccessor = sp.GetRequiredService<IConversationSessionAccessor>();
            var workspaceService = sp.GetService<IWorkspaceService>();
            var functionRegistry = sp.GetRequiredService<IFunctionRegistry>();
            var mcpToolHost = sp.GetService<Athena.UI.Services.Mcp.IMcpToolHost>();
            var skillCatalog = sp.GetService<ISkillCatalogService>();
            var metadataCatalog = sp.GetService<IOpenRouterModelMetadataCatalog>();
            var metadataResolver = sp.GetService<IModelMetadataResolver>();
            var contextPolicyResolver = sp.GetService<IModelContextPolicyResolver>();
            var providerErrorClassifier = sp.GetService<IProviderErrorClassifier>();
            var requestPreparer = sp.GetService<IContextRequestPreparer>();
            var tokenCalibration = sp.GetService<ITokenCalibrationService>();
            var compressionPlanner = sp.GetService<ICompressionPlanner>();
            var compressionCandidateGenerator = sp.GetService<ICompressionCandidateGenerator>();
            var compressionValidator = sp.GetService<ICompressionValidator>();
            var contextPolicyProvider = sp.GetService<IContextPolicyProvider>();
            var config = configService.Load();
            var mainProvider = config.AiModels.Providers.FirstOrDefault(provider =>
                provider.Id == config.AiModels.MainConversation.ProviderId);
            Log.Information("AI service initialized, provider configuration: {ProviderId}, model: {Model}, FunctionCalling: {Enabled}",
                mainProvider?.ProviderPreset ?? "(not configured)",
                config.AiModels.MainConversation.Model,
                true);
            var service = new OpenAIChatService(
                config,
                promptService,
                null,
                locationService,
                attachmentStoreService,
                conversationSessionAccessor,
                workspaceService,
                configService,
                functionRegistry,
                mcpToolHost,
                skillCatalog,
                metadataCatalog,
                metadataResolver,
                contextPolicyResolver,
                providerErrorClassifier,
                requestPreparer,
                tokenCalibration,
                compressionPlanner,
                compressionCandidateGenerator,
                compressionValidator,
                contextPolicyProvider);
            return service;
        });

        services.AddSingleton<IConversationArchiveService>(sp =>
        {
            var store = sp.GetRequiredService<IConversationArchiveStore>();
            var draftStore = sp.GetRequiredService<IConversationDraftStore>();
            var titleGenerator = sp.GetRequiredService<IConversationTitleGenerator>();
            var platformPathService = sp.GetRequiredService<IPlatformPathService>();
            var logger = Log.ForContext<ConversationArchiveService>();
            var imageSessionService = sp.GetService<IImageGenerationSessionService>();
            return new ConversationArchiveService(store, draftStore, titleGenerator, platformPathService, logger, imageSessionService);
        });

        // ViewModels
        services.AddSingleton<MainWindowViewModel>(sp =>
        {
            var chatService = sp.GetService<IChatService>();
            var configService = sp.GetService<IConfigService>();
            var taskScheduler = sp.GetService<ITaskScheduler>();
            var promptService = sp.GetService<IPromptService>();
            var logService = sp.GetService<ILogService>();
            var knowledgeBaseService = sp.GetService<IKnowledgeBaseService>();
            var localizationService = sp.GetService<ILocalizationService>();
            var fileSystemService = sp.GetService<IFileSystemService>();
            var platformPathService = sp.GetRequiredService<IPlatformPathService>();
            var functionRegistry = sp.GetService<IFunctionRegistry>();
            var tokenService = sp.GetService<ITokenService>();
            var attachmentStoreService = sp.GetService<IAttachmentStoreService>();
            var systemAudioService = sp.GetService<ISystemAudioService>();
            var archiveService = sp.GetService<IConversationArchiveService>();
            var imageGenerationSessionService = sp.GetService<IImageGenerationSessionService>();
            var screenCaptureService = sp.GetService<IScreenCaptureService>();
            var subAgentOrchestrator = sp.GetService<ISubAgentOrchestrator>();
            var knowledgeMaintenanceService = sp.GetService<IKnowledgeBaseMaintenanceService>();
            var workspaceService = sp.GetService<IWorkspaceService>();
            var conversationSessionAccessor = sp.GetService<IConversationSessionAccessor>();
            var userInteractionService = sp.GetService<IUserInteractionService>();
            var executionCoordinator = sp.GetRequiredService<ConversationExecutionCoordinator>();
            var chatSessionFactory = sp.GetRequiredService<ChatSessionFactory>();
            var conversationStore = sp.GetRequiredService<IConversationArchiveStore>();
            var workbench = sp.GetRequiredService<WorkspaceWorkbenchViewModel>();
            var approvalQueue = sp.GetRequiredService<ApprovalQueueViewModel>();
            var configurationSession = sp.GetRequiredService<AppConfigurationSession>();
            var terminalPanelViewModel = sp.GetRequiredService<TerminalPanelViewModel>();
            var contextPolicyProvider = sp.GetRequiredService<IContextPolicyProvider>();
            _ = sp.GetRequiredService<AppConfigurationApplier>();
            Func<SkillsConnectorsWindowViewModel> skillsConnectorsFactory =
                () => sp.GetRequiredService<SkillsConnectorsWindowViewModel>();
            Func<AppSettingsWindowViewModel> appSettingsFactory =
                () => sp.GetRequiredService<AppSettingsWindowViewModel>();

            return new MainWindowViewModel(
                chatService,
                configService,
                taskScheduler,
                null,
                promptService,
                logService,
                knowledgeBaseService,
                localizationService,
                fileSystemService,
                platformPathService,
                functionRegistry,
                tokenService,
                attachmentStoreService,
                systemAudioService,
                archiveService,
                imageGenerationSessionService,
                screenCaptureService,
                subAgentOrchestrator,
                knowledgeMaintenanceService,
                workspaceService,
                conversationSessionAccessor,
                userInteractionService,
                executionCoordinator,
                chatSessionFactory,
                conversationStore,
                workbench,
                approvalQueue,
                configurationSession,
                skillsConnectorsFactory,
                appSettingsFactory,
                terminalPanelViewModel,
                contextPolicyProvider,
                sp.GetRequiredService<IConversationTitleGenerator>());
        });

        Log.Debug("Dependency injection services configured");
    }
}
