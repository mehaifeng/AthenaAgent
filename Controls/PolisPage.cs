using Athena.UI.ViewModels.GameMode;
using Avalonia.Controls;
using Avalonia.Layout;
using Serilog;
using System;
using System.Threading.Tasks;

namespace Athena.UI.Controls;

/// <summary>
/// 承载游戏页面的那一块：真实环境是 <see cref="NativeWebView"/>，无头测试里是一个假页面（不实例化任何平台 WebView）。
/// 视图只认这个接口；ViewModel 只认更窄的 <see cref="IPolisPageChannel"/>。
/// </summary>
internal interface IPolisPage : IPolisPageChannel, IDisposable
{
    Control Control { get; }

    /// <summary>顶层文档导航结束（参数：是否成功）。</summary>
    event EventHandler<bool>? NavigationCompleted;

    /// <summary>页面经 invokeCSharpAction 发来的一条消息（原文，校验在 ViewModel 里做）。</summary>
    event EventHandler<string?>? MessageReceived;
}

/// <summary>
/// 页面的构造缝。生产路径是 <see cref="NativeWebViewPolisPage"/>；无头测试换成假页面，模式切换与四处失败提示
/// 因此都能在不创建 WebView 的情况下断言（与 Office 预览"Url 为空就不建 WebView"是同一个做法）。
/// </summary>
internal static class PolisPageFactory
{
    internal static Func<Uri, IPolisPage> Create { get; set; } = static uri => new NativeWebViewPolisPage(uri);
}

/// <summary>NativeWebView 包装：导航、脚本推送、消息接收。挂载失败的兜底见 <see cref="HandleAttachFailure"/>。</summary>
internal sealed class NativeWebViewPolisPage : IPolisPage
{
    private static readonly ILogger Logger = Log.ForContext<NativeWebViewPolisPage>();

    /// <summary>正在挂载、还没完成首次导航的那一个页面（只在 UI 线程上读写）。</summary>
    private static NativeWebViewPolisPage? _pendingAttach;

    private readonly NativeWebView _webView;
    private bool _navigated;
    private bool _disposed;

    public NativeWebViewPolisPage(Uri uri)
    {
        _webView = new NativeWebView
        {
            Source = uri,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _webView.NavigationCompleted += OnNavigationCompleted;
        _webView.WebMessageReceived += OnWebMessageReceived;
        // 挂载是异步的（NativeWebView.OnAttached 经调度器回抛），挂载失败时靠它找到是哪一个页面失败了
        _pendingAttach = this;
    }

    public Control Control => _webView;

    public event EventHandler<bool>? NavigationCompleted;

    public event EventHandler<string?>? MessageReceived;

    /// <summary>挂载阶段的失败（第 1 处的异步那一半）：由 App 的调度器未处理异常兜底转来。</summary>
    public event EventHandler<Exception>? AttachFailed;

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (!_navigated)
        {
            _navigated = true;
            if (ReferenceEquals(_pendingAttach, this)) _pendingAttach = null;
        }
        NavigationCompleted?.Invoke(this, e.IsSuccess);
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e) => MessageReceived?.Invoke(this, e.Body);

    public async Task InvokeScriptAsync(string script)
    {
        if (_disposed) return;
        await _webView.InvokeScript(script);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(_pendingAttach, this)) _pendingAttach = null;
        _webView.NavigationCompleted -= OnNavigationCompleted;
        _webView.WebMessageReceived -= OnWebMessageReceived;
        try
        {
            _webView.Stop();
        }
        catch (InvalidOperationException ex)
        {
            // 从没挂上过的 WebView 停不下来：它本来就没在跑
            Logger.Debug(ex, "Stopping the polis web view failed");
        }
    }

    /// <summary>
    /// 原生挂载阶段的失败。和 Office 预览一样，它发生在 async-void 的调度器延续里，视图代码 try/catch 不到，
    /// 只能由 App 的 Dispatcher.UnhandledException 兜底转到这里。只认"有一个游戏页面正在挂载、而且异常像是 WebView
    /// 自己的失败"——WebView2 运行时缺失、Win32 原生宿主建不起来，以及 Linux 上 WebKitGTK / GTK 的库加载失败。
    /// </summary>
    /// <returns>是否认领了这个异常（调用方据此标记为已处理）。</returns>
    internal static bool HandleAttachFailure(Exception? ex)
    {
        var pending = _pendingAttach;
        if (pending == null || !IsNativeWebViewFailure(ex)) return false;
        _pendingAttach = null;
        try
        {
            pending.AttachFailed?.Invoke(pending, ex!);
        }
        catch (Exception secondary)
        {
            // 兜底处理自身不能再抛，否则会重新变成未处理异常
            Logger.Debug(secondary, "Reporting a polis web view attach failure failed");
        }
        return true;
    }

    private static bool IsNativeWebViewFailure(Exception? ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is DllNotFoundException or EntryPointNotFoundException) return true;
            var text = current.GetType().Name + " " + current.Message;
            if (text.Contains("native control host", StringComparison.OrdinalIgnoreCase)
                || text.Contains("WebView", StringComparison.OrdinalIgnoreCase)
                || text.Contains("webkit", StringComparison.OrdinalIgnoreCase)
                || text.Contains("gtk", StringComparison.OrdinalIgnoreCase)
                || text.Contains("X11", StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
