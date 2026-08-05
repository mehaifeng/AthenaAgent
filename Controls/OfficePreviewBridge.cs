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
        border.Child = webView;
        border.DetachedFromVisualTree += OnBorderDetached;
    }

    private static void Detach(Border border)
    {
        if (!States.TryGetValue(border, out var state)) return;
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
}
