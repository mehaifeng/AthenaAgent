using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Services;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Serilog;

namespace Athena.UI.Views;

public partial class MainWindow
{
    private bool _onboardingEntrancePrepared;
    private bool _onboardingEntrancePlayed;
    private CancellationTokenSource? _onboardingEntranceCancellation;
    private Control[] _onboardingEntrancePanels = [];

    /// <summary>Hide the shell before Show, so its first frame cannot flash at full opacity.</summary>
    public void PrepareOnboardingEntrance()
    {
        if (_onboardingEntrancePrepared || _onboardingEntrancePlayed) return;
        if (_mainShellGrid == null || this.FindControl<Border>("ConversationPanel") == null)
            throw new InvalidOperationException("The main shell is required for the onboarding entrance.");

        _onboardingEntrancePanels = new[] { "ConversationPanel", "LeftPanel", "RightPanelGrid", "LeftSideSplitter", "RightSideSplitter" }
            .Select(name => this.FindControl<Control>(name)
                ?? throw new InvalidOperationException($"The onboarding entrance requires {name}."))
            .ToArray();
        _onboardingEntrancePrepared = true;
        foreach (var panel in _onboardingEntrancePanels)
            panel.Opacity = 0;
        _mainShellGrid.IsHitTestVisible = false;
        Opened += OnOnboardingEntranceOpened;
    }

    private void OnOnboardingEntranceOpened(object? sender, EventArgs e)
    {
        Opened -= OnOnboardingEntranceOpened;
        // Wait for the render pipeline, not a guessed delay; startup layout must not consume the motion.
        RequestAnimationFrame(_ => AsyncEventGuard.Run(PlayOnboardingEntranceAsync, nameof(OnOnboardingEntranceOpened)));
    }

    private void CancelOnboardingEntrance()
    {
        if (_onboardingEntranceCancellation != null) _onboardingEntranceCancellation.Cancel();
        else if (_onboardingEntrancePrepared)
        {
            _onboardingEntrancePlayed = true;
            RestoreOnboardingEntrance();
        }
    }

    private void RestoreOnboardingEntrance()
    {
        if (_mainShellGrid == null) return;
        foreach (var panel in _onboardingEntrancePanels)
        {
            panel.ClearValue(OpacityProperty);
            panel.ClearValue(RenderTransformProperty);
        }
        _mainShellGrid.IsHitTestVisible = true;
        _onboardingEntrancePrepared = false;
    }

    private async Task PlayOnboardingEntranceAsync()
    {
        if (!_onboardingEntrancePrepared || _onboardingEntrancePlayed || _mainShellGrid == null) return;
        _onboardingEntrancePlayed = true;
        using var cancellation = new CancellationTokenSource();
        _onboardingEntranceCancellation = cancellation;
        try
        {
            // Use physical columns, so swapping the two side panels preserves the motion's direction.
            // Collapsed panels retain their IsVisible binding; no layout, window bounds or theme changes.
            await Task.WhenAll(_onboardingEntrancePanels.Select(panel =>
            {
                var column = Grid.GetColumn(panel);
                var center = column == 2;
                var left = column < 2;
                return AnimateEntrancePanelAsync(panel, center, center ? 10 : left ? -12 : 12,
                    center ? 0 : left ? 50 : 100, center ? 250 : 230, cancellation.Token);
            }));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Closing to the tray during the entrance must leave a fully usable shell on restore.
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Onboarding panel entrance failed; showing the completed shell");
        }
        finally
        {
            RestoreOnboardingEntrance();
            _onboardingEntranceCancellation = null;
        }
    }

    private static async Task AnimateEntrancePanelAsync(Control panel, bool vertical, double offset,
        int delayMs, int durationMs, CancellationToken cancellationToken)
    {
        if (!panel.IsVisible) return;
        var movement = vertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;
        // Transform keyframes target the Visual, as in the existing workbench entrance style.
        // Both opacity and translation share one clock; backward fill covers the side-panel delays.
        panel.RenderTransform = new TranslateTransform();
        panel.Opacity = 1;
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(durationMs),
            Delay = TimeSpan.FromMilliseconds(delayMs),
            FillMode = FillMode.Backward,
            Easing = new CubicEaseOut(),
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters = { new Setter(OpacityProperty, 0d), new Setter(movement, offset) }
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters = { new Setter(OpacityProperty, 1d), new Setter(movement, 0d) }
                }
            }
        };
        await animation.RunAsync(panel, cancellationToken);
    }
}
