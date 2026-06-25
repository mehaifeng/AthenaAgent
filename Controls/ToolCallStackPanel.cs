using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Athena.UI.Controls;

/// <summary>
/// 工具调用卡片的折叠面板（自定义布局实现）：
/// 收起时只占「前 PeekCount 个卡片 + 第 PeekCount+1 个的上半」的高度，子项溢出被裁剪并在底部朦胧渐隐；
/// 展开时占自然全高。收起/展开通过 <see cref="RenderHeightProperty"/> 由动画时钟驱动，平滑过渡。
/// 高度全部在 MeasureOverride 中以无限约束测量子项得到，绝不在布局事件里回写属性，避免无限布局循环。
/// </summary>
public class ToolCallStackPanel : Panel
{
    private static readonly IBrush FadeMask = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Colors.White, 0),
            new GradientStop(Colors.White, 0.72),
            new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0)
        }
    };

    public static readonly StyledProperty<bool> IsCollapsedProperty =
        AvaloniaProperty.Register<ToolCallStackPanel, bool>(nameof(IsCollapsed));

    public static readonly StyledProperty<int> PeekCountProperty =
        AvaloniaProperty.Register<ToolCallStackPanel, int>(nameof(PeekCount), 3);

    public static readonly StyledProperty<double> ItemSpacingProperty =
        AvaloniaProperty.Register<ToolCallStackPanel, double>(nameof(ItemSpacing), 6);

    /// <summary>渲染高度（NaN 表示用自然测量高度）。动画期间由过渡驱动。</summary>
    public static readonly StyledProperty<double> RenderHeightProperty =
        AvaloniaProperty.Register<ToolCallStackPanel, double>(nameof(RenderHeight), double.NaN);

    static ToolCallStackPanel()
    {
        AffectsMeasure<ToolCallStackPanel>(RenderHeightProperty, IsCollapsedProperty, PeekCountProperty, ItemSpacingProperty);
    }

    private double _fullHeight;
    private double _collapsedHeight;
    private bool _hasMeasured;
    private DispatcherTimer? _releaseTimer;

    public ToolCallStackPanel()
    {
        ClipToBounds = true;
        Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = RenderHeightProperty,
                Duration = TimeSpan.FromMilliseconds(220),
                Easing = new CubicEaseOut()
            }
        };
    }

    public bool IsCollapsed
    {
        get => GetValue(IsCollapsedProperty);
        set => SetValue(IsCollapsedProperty, value);
    }

    public int PeekCount
    {
        get => GetValue(PeekCountProperty);
        set => SetValue(PeekCountProperty, value);
    }

    public double ItemSpacing
    {
        get => GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    public double RenderHeight
    {
        get => GetValue(RenderHeightProperty);
        set => SetValue(RenderHeightProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsCollapsedProperty)
        {
            OpacityMask = IsCollapsed ? FadeMask : null;
            AnimateToState();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = availableSize.Width;
        double spacing = ItemSpacing;
        int peek = PeekCount;
        int n = Children.Count;

        double full = 0;
        double collapsed = 0;
        double maxChildWidth = 0;

        for (int i = 0; i < n; i++)
        {
            var child = Children[i];
            child.Measure(new Size(width, double.PositiveInfinity));
            double h = child.DesiredSize.Height;
            maxChildWidth = Math.Max(maxChildWidth, child.DesiredSize.Width);

            double top = full + (i > 0 ? spacing : 0);
            full = top + h;

            if (i < peek)
            {
                collapsed = full;             // 前 peek 个卡片完整显示
            }
            else if (i == peek)
            {
                collapsed = top + h * 0.5;     // 第 peek+1 个露上半 peek
            }
        }

        _fullHeight = full;
        _collapsedHeight = (n > peek) ? collapsed : full;
        _hasMeasured = true;

        double rh = RenderHeight;
        double target = double.IsNaN(rh)
            ? (IsCollapsed && n > peek ? _collapsedHeight : _fullHeight)
            : rh;

        double w = double.IsInfinity(width) ? maxChildWidth : width;
        return new Size(w, target);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double spacing = ItemSpacing;
        double y = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (i > 0)
            {
                y += spacing;
            }

            double h = child.DesiredSize.Height;
            child.Arrange(new Rect(0, y, finalSize.Width, h));
            y += h;
        }

        return finalSize;
    }

    // 收起/展开态切换：以当前目标为起点瞬时落位，再过渡到新目标，结束后释放为自然高度。
    private void AnimateToState()
    {
        if (!_hasMeasured || Children.Count <= PeekCount || _fullHeight <= 0)
        {
            SetHeightInstant(double.NaN);
            return;
        }

        double start = IsCollapsed ? _fullHeight : _collapsedHeight;
        double end = IsCollapsed ? _collapsedHeight : _fullHeight;

        SetHeightInstant(start);
        SetCurrentValue(RenderHeightProperty, end); // 带过渡，动画到目标
        ScheduleRelease();
    }

    private void SetHeightInstant(double value)
    {
        var transitions = Transitions;
        Transitions = null;
        SetCurrentValue(RenderHeightProperty, value);
        Transitions = transitions;
    }

    private void ScheduleRelease()
    {
        _releaseTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
        _releaseTimer.Stop();
        _releaseTimer.Tick -= OnReleaseTick;
        _releaseTimer.Tick += OnReleaseTick;
        _releaseTimer.Start();
    }

    // 动画结束后切回自然测量高度：展开态贴合内容（支持单卡详情增高），收起态贴合收起高度。
    private void OnReleaseTick(object? sender, EventArgs e)
    {
        _releaseTimer?.Stop();
        SetHeightInstant(double.NaN);
    }
}
