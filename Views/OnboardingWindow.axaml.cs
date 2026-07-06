using Avalonia.Controls;
using Avalonia.Interactivity;
using Athena.UI.ViewModels;

namespace Athena.UI.Views;

public partial class OnboardingWindow : Window
{
    public OnboardingWindow()
    {
        InitializeComponent();
    }

    public OnboardingWindow(OnboardingViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.RequestClose = Close;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is OnboardingViewModel vm)
        {
            vm.RequestClose = null;
        }
        base.OnClosing(e);
    }

    /// <summary>Provider 选择变化时带出默认 BaseUrl（避免用户手抄端点）。</summary>
    private void OnProviderSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is OnboardingViewModel vm && vm.ApplyProviderDefaultUrlCommand.CanExecute(null))
        {
            vm.ApplyProviderDefaultUrlCommand.Execute(null);
        }
    }

    /// <summary>Web Search 供应商选择变化：带出默认 BaseUrl 并刷新 Custom / Baidu 附加字段可见性。</summary>
    private void OnWebSearchProviderSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is OnboardingViewModel vm && vm.ApplyWebSearchProviderDefaultUrlCommand.CanExecute(null))
        {
            vm.ApplyWebSearchProviderDefaultUrlCommand.Execute(null);
        }
    }
}
