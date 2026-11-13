using Avalonia.Controls;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Styling;
using Athena.UI.Services;
using Serilog;
using System.Threading;
using Athena.UI.ViewModels;
using System;
using System.Threading.Tasks;

namespace Athena.UI.Views;

public partial class OnboardingWindow : Window
{
    /// <summary>
    /// 引导完成时的交接回调（由 App 注入）：负责准备三栏入场并在原位显示主窗口。
    /// 为空时退化为直接 Close（等价旧行为）。
    /// </summary>
    public Func<Task>? HandoffRequested { get; set; }

    private bool _handoffStarted;
    private CancellationTokenSource? _handoffCancellation;
    internal bool HandoffWasCancelled { get; private set; }

    public OnboardingWindow()
    {
        InitializeComponent();
    }

    public OnboardingWindow(OnboardingViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.RequestClose = OnRequestClose;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Cancel before detaching the visual tree: detachment itself can complete RunAsync,
        // which would otherwise let the awaiting handoff show the main window after a close.
        if (_handoffCancellation != null)
        {
            HandoffWasCancelled = true;
            _handoffCancellation.Cancel();
        }
        if (DataContext is OnboardingViewModel vm)
        {
            vm.RequestClose = null;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _handoffCancellation?.Cancel();
        if (DataContext is IDisposable disposable) disposable.Dispose();
        base.OnClosed(e);
    }

    /// <summary>完成/跳过按钮的关窗请求：有交接回调则走三栏入场流程（App 全程编排），否则直接关窗。</summary>
    private void OnRequestClose()
    {
        if (_handoffStarted) return;

        if (HandoffRequested is { } handoff)
        {
            _handoffStarted = true;
            var card = this.FindControl<Border>("OnboardingCard");
            if (card != null) card.IsHitTestVisible = false;
            AsyncEventGuard.Run(async () =>
            {
                try { await handoff(); }
                catch
                {
                    // A failed window construction/show must leave the setup form usable for retry.
                    _handoffStarted = false;
                    if (card != null)
                    {
                        card.Opacity = 1;
                        card.IsHitTestVisible = true;
                    }
                    throw;
                }
            }, nameof(OnRequestClose));
        }
        else
        {
            Close();
        }
    }

    /// <summary>设置卡片在原位淡出；整个交接不改变窗口尺寸、位置或背景裁切。</summary>
    public async Task PlayHandoffExitAsync()
    {
        var card = this.FindControl<Border>("OnboardingCard")
            ?? throw new InvalidOperationException("The onboarding card is required for the handoff.");
        using var cancellation = new CancellationTokenSource();
        _handoffCancellation = cancellation;
        var cancellationToken = cancellation.Token;
        card.IsHitTestVisible = false;
        card.Opacity = 0;
        try
        {
            var fade = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(150),
                Easing = new CubicEaseOut(),
                Children =
                {
                    new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 1d) } },
                    new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0d) } }
                }
            };
            await fade.RunAsync(card, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Losing a decorative fade must not prevent entering the working application.
            Log.Warning(ex, "Onboarding card fade failed; continuing the handoff");
        }
        finally
        {
            _handoffCancellation = null;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Provider 选择变化时带出默认 BaseUrl（避免用户手抄端点）。</summary>
    private void OnProviderSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is OnboardingViewModel vm && vm.ApplyProviderDefaultUrlCommand.CanExecute(null))
        {
            vm.ApplyProviderDefaultUrlCommand.Execute(null);
        }
    }

}
