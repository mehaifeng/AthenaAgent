using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Styling;
using Serilog;

namespace Athena.UI.Controls;

/// <summary>
/// Office 预览的 NativeWebView 惰性创建桥（附加属性行为）。
/// 挂在编辑器 DataTemplate 的 Border 占位上：Url 为空时【不创建任何 WebView】——
/// 这是 headless 测试安全的关键（非 Office tab 永远不会实例化平台 WebView 控件）。
///
/// 注意：ContentControl 对同类型 tab 会复用模板实例（Border 是同一对象），
/// 因此 Url 变化时必须重新导航（复用 WebView 更新 Source），
/// 否则 WebView 会停留在上一个文件的页面。
///
/// 主题：页面初始取打开时的主题值（BuildPreviewUrl 的 theme 参数），
/// 导航完成后与 App.ThemeChanged 时经 InvokeScript 推送 setTheme，跟随全局主题切换。
/// </summary>
public static class OfficePreviewBridge
{
    private static readonly ILogger Logger = Log.ForContext(typeof(OfficePreviewBridge));

    public static readonly AttachedProperty<string?> UrlProperty =
        AvaloniaProperty.RegisterAttached<Border, string?>(
            "Url", typeof(OfficePreviewBridge), null);

    public static string? GetUrl(Border element) => element.GetValue(UrlProperty);

    public static void SetUrl(Border element, string? value) => element.SetValue(UrlProperty, value);

    /// <summary>WebView 创建或加载失败（参数为关联的 Border 与异常，异常可为 null）。</summary>
    public static event Action<Border, Exception?>? Failed;

    private sealed class WebViewState(NativeWebView webView)
    {
        public NativeWebView WebView { get; } = webView;

        /// <summary>顶层文档导航是否已成功完成（此后才推送主题，避免脚本未就绪）。</summary>
        public bool Loaded { get; set; }
    }

    private static readonly ConditionalWeakTable<Border, WebViewState> States = new();

    /// <summary>正在挂载（已 attach 但尚未完成首次导航）的 WebView 状态；仅 UI 线程访问。</summary>
    private static WebViewState? _pendingAttach;

    static OfficePreviewBridge()
    {
        UrlProperty.Changed.AddClassHandler<Border>(OnUrlChanged);
    }

    private static void OnUrlChanged(Border border, AvaloniaPropertyChangedEventArgs e)
    {
        var url = e.GetNewValue<string?>();
        if (string.IsNullOrWhiteSpace(url))
        {
            Detach(border);
            return;
        }

        if (States.TryGetValue(border, out var existing))
        {
            // 模板复用：同一 Border 上切换到另一个 office 文件，导航到新 URL。
            // 旧的导航若未完成会被新导航取代（WebView 内部处理）。
            existing.Loaded = false;
            existing.WebView.Source = new Uri(url);
            return;
        }
        Attach(border, url);
    }

    private static void Attach(Border border, string url)
    {
        NativeWebView webView;
        try
        {
            webView = new NativeWebView
            {
                Source = new Uri(url),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to create NativeWebView for {Url}", url);
            Failed?.Invoke(border, ex);
            return;
        }

        var state = new WebViewState(webView);
        States.Add(border, state);
        webView.NavigationCompleted += OnNavigationCompleted;
        App.ThemeChanged += OnThemeChanged;
        // 挂载是异步的（NativeWebView.OnAttached 经调度器回抛），
        // 挂载失败时靠 _pendingAttach 定位失败的 WebView（HandleAttachFailure）。
        _pendingAttach = state;
        border.Child = webView;
        border.DetachedFromVisualTree += OnBorderDetached;
    }

    private static void Detach(Border border)
    {
        if (!States.TryGetValue(border, out var state)) return;
        if (ReferenceEquals(_pendingAttach, state)) _pendingAttach = null;
        States.Remove(border);
        state.WebView.NavigationCompleted -= OnNavigationCompleted;
        App.ThemeChanged -= OnThemeChanged;
        state.WebView.Stop();
        border.DetachedFromVisualTree -= OnBorderDetached;
        border.Child = null;
    }

    private static void OnBorderDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Border border) Detach(border);
    }

    private static void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (sender is not NativeWebView webView) return;
        var border = FindBorder(webView);
        if (border == null || !States.TryGetValue(border, out var state)) return;
        if (!e.IsSuccess)
        {
            Logger.Debug("WebView navigation failed: {Uri}", e.Request);
            Detach(border);
            Failed?.Invoke(border, null);
            return;
        }
        state.Loaded = true;
        if (ReferenceEquals(_pendingAttach, state)) _pendingAttach = null;
        PushTheme(state);
    }

    private static void OnThemeChanged(string theme)
    {
        foreach (var (_, state) in States)
        {
            if (state.Loaded) PushTheme(state, theme);
        }
    }

    private static void PushTheme(WebViewState state) =>
        PushTheme(state, GetCurrentTheme());

    private static void PushTheme(WebViewState state, string theme)
    {
        // 页面脚本定义 window.setTheme（viewer.js），未就绪时抛错被吞——
        // 初始主题已由 URL 参数生效，此处仅保证运行时切换与导航完成后的同步。
        _ = PushThemeAsync(state.WebView, $"setTheme('{(theme == "Dark" ? "dark" : "light")}')");
    }

    private static async Task PushThemeAsync(NativeWebView webView, string script)
    {
        try
        {
            await webView.InvokeScript(script);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to push theme to office preview webview");
        }
    }

    private static string GetCurrentTheme()
        => Equals(Application.Current?.RequestedThemeVariant, ThemeVariant.Dark) ? "Dark" : "Light";

    private static Border? FindBorder(NativeWebView webView)
    {
        foreach (var (border, state) in States)
        {
            if (ReferenceEquals(state.WebView, webView)) return border;
        }
        return null;
    }

    /// <summary>
    /// 处理 NativeWebView 原生挂载阶段的失败。挂载发生在 async-void 调度器延续里
    /// （NativeWebView.OnAttached 经 Dispatcher 回抛），附加属性代码无法 try/catch，
    /// 只能由 App 的 Dispatcher.UnhandledException 兜底回调本方法。
    /// 命中原生挂载类失败时清理 WebView 并触发 <see cref="Failed"/> 回退为二进制占位。
    /// </summary>
    /// <returns>异常是否属于原生挂载失败（调用方据此决定是否标记为已处理）。</returns>
    internal static bool HandleAttachFailure(Exception? ex)
    {
        if (!IsNativeAttachFailure(ex)) return false;
        try
        {
            var pending = _pendingAttach;
            if (pending == null) return true;
            var border = FindBorder(pending.WebView);
            if (border == null) return true;
            Detach(border);
            Failed?.Invoke(border, ex);
        }
        catch (Exception secondary)
        {
            // 兜底清理自身不能再抛，否则会重新变成未处理异常
            Logger.Debug(secondary, "Failed to clean up office preview webview after attach failure");
        }
        return true;
    }

    private static bool IsNativeAttachFailure(Exception? ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            // Win32NativeControlHost.DumbWindow：应用清单缺少 supportedOS 时 CreateWindowEx 失败
            if (current is InvalidOperationException
                && current.Message.Contains("native control host", StringComparison.OrdinalIgnoreCase))
                return true;
            // WebView2 运行时缺失 / 环境初始化失败（同样发生在挂载阶段）
            if (current.GetType().Name.Contains("WebView2", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("WebView2", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
