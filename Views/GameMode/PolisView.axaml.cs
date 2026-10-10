using Athena.UI.Controls;
using Athena.UI.ViewModels.GameMode;
using Avalonia.Controls;
using Avalonia.Threading;
using System;
using System.ComponentModel;

namespace Athena.UI.Views.GameMode;

/// <summary>
/// 游戏视图：按 <see cref="GameModeViewModel.PageUrl"/> 惰性创建 / 拆掉页面（第一次进入游戏模式才建 WebView），
/// 把页面的导航结果、消息和挂载失败交给 ViewModel。四处失败的判断与提示都在 ViewModel 里，这里只是传话。
/// </summary>
public partial class PolisView : UserControl
{
    private GameModeViewModel? _viewModel;
    private IPolisPage? _page;
    private string? _pageUrl;

    public PolisView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind(DataContext as GameModeViewModel);
        AttachedToVisualTree += (_, _) => UpdatePage();
        DetachedFromVisualTree += (_, _) => DisposePage();
    }

    private void Bind(GameModeViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel)) return;
        if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        DisposePage();
        _viewModel = viewModel;
        if (_viewModel != null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdatePage();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameModeViewModel.PageUrl)) UpdatePage();
    }

    private void UpdatePage()
    {
        if (_viewModel == null || VisualRoot == null) return;
        var url = _viewModel.PageUrl;
        if (url == _pageUrl && (_page != null || url == null)) return;
        DisposePage();
        if (url == null) return;

        var host = this.FindControl<Border>("PolisPageHost")!;
        IPolisPage page;
        try
        {
            page = PolisPageFactory.Create(new Uri(url));
        }
        catch (Exception ex)
        {
            // 第 1 处（同步那一半）：WebView 构造就失败了
            _viewModel.OnPageCreateFailed(ex);
            return;
        }

        _page = page;
        _pageUrl = url;
        page.NavigationCompleted += OnNavigationCompleted;
        page.MessageReceived += OnMessageReceived;
        if (page is NativeWebViewPolisPage native) native.AttachFailed += OnAttachFailed;
        host.Child = page.Control;
        _viewModel.AttachPage(page);
    }

    private void DisposePage()
    {
        var page = _page;
        if (page == null) return;
        _page = null;
        _pageUrl = null;
        page.NavigationCompleted -= OnNavigationCompleted;
        page.MessageReceived -= OnMessageReceived;
        if (page is NativeWebViewPolisPage native) native.AttachFailed -= OnAttachFailed;
        var host = this.FindControl<Border>("PolisPageHost");
        if (host != null) host.Child = null;
        _viewModel?.DetachPage(page);
        page.Dispose();
    }

    private void OnNavigationCompleted(object? sender, bool success)
        => OnUiThread(() => { if (ReferenceEquals(sender, _page)) _viewModel?.OnNavigationCompleted(success); });

    private void OnMessageReceived(object? sender, string? body)
        => OnUiThread(() => { if (ReferenceEquals(sender, _page)) _viewModel?.OnPageMessage(body); });

    private void OnAttachFailed(object? sender, Exception error)
        => OnUiThread(() => { if (ReferenceEquals(sender, _page)) _viewModel?.OnPageCreateFailed(error); });

    private static void OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}
