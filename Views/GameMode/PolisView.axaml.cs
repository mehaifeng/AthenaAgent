using Athena.UI.Controls;
using Athena.UI.ViewModels.GameMode;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
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

    // 页面出来之前（加载中、失败面板后面）的底色：和城里的白天 / 夜里同色，切模式时不闪一块别的颜色
    private static readonly IBrush DayBackground = new SolidColorBrush(Color.Parse("#C8C5BF"));
    private static readonly IBrush NightBackground = new SolidColorBrush(Color.Parse("#15181D"));
    private static readonly IBrush DayText = new SolidColorBrush(Color.Parse("#4A433D"));
    private static readonly IBrush NightText = new SolidColorBrush(Color.Parse("#B8B0A2"));

    public PolisView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind(DataContext as GameModeViewModel);
        AttachedToVisualTree += (_, _) => { SyncTheme(); UpdatePage(); };
        DetachedFromVisualTree += (_, _) => DisposePage();
        // 应用换了深 / 浅色主题：主题变体一落地就告诉游戏（不等 App.ThemeChanged——它在 1.2 秒的背景过渡之后才广播）
        ActualThemeVariantChanged += (_, _) => SyncTheme();
    }

    private void Bind(GameModeViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel)) return;
        if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        DisposePage();
        _viewModel = viewModel;
        if (_viewModel != null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        SyncTheme();
        UpdatePage();
    }

    /// <summary>夜里（游戏的夜跟着应用的深色主题）：底色、加载提示的字色，以及告诉 ViewModel。没挂到界面上时主题变体读不准，不动。</summary>
    private void SyncTheme()
    {
        if (VisualRoot == null) return;
        var night = ActualThemeVariant == ThemeVariant.Dark;
        var surface = this.FindControl<Grid>("PolisSurface");
        if (surface != null) surface.Background = night ? NightBackground : DayBackground;
        var loading = this.FindControl<TextBlock>("PolisLoadingText");
        if (loading != null) loading.Foreground = night ? NightText : DayText;
        _viewModel?.SetNight(night);
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
