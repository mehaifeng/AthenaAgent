using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Athena.UI.Models;
using Athena.UI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;

namespace Athena.UI.Controls;

/// <summary>One lazily opened drawing surface, not thousands of controls in the non-virtualized chat tree.</summary>
public sealed class ExecutionTimelineChart : Control
{
    public static readonly StyledProperty<ExecutionTimelineSnapshot?> SnapshotProperty =
        AvaloniaProperty.Register<ExecutionTimelineChart, ExecutionTimelineSnapshot?>(nameof(Snapshot));
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<ExecutionTimelineChart, double>(nameof(Zoom), 1d);
    public static readonly StyledProperty<int> LanguageVersionProperty =
        AvaloniaProperty.Register<ExecutionTimelineChart, int>(nameof(LanguageVersion));

    private const double Left = 145;
    private const double Header = 40;
    private const double RowHeight = 27;
    private const double MinLaneHeight = 40;
    private const double GroupGap = 12;
    private double _viewportWidth = 900, _viewportHeight = 600, _offsetX, _offsetY;
    private readonly List<Bar> _bars = [];
    private readonly List<(ExecutionTimelineLane Lane, double Top, double Height)> _lanes = [];
    private long? _selected;
    private long? _hovered;
    public sealed record Bar(ExecutionTimelineSpan Span, Rect Bounds);
    internal sealed record LaneLabel(ExecutionTimelineLane Lane, string Title, string Description,
        Rect LaneBounds, Rect TitleBounds, Rect DescriptionBounds);
    public IReadOnlyList<Bar> Bars => _bars;
    internal IReadOnlyList<LaneLabel> LaneLabels => _lanes.Select(lane => new LaneLabel(
        lane.Lane,
        LaneName(lane.Lane),
        DataContext is ExecutionTimelineViewModel vm ? vm.LaneDescription(lane.Lane) : ExecutionTimelineViewModel.DefaultLaneDescription(lane.Lane),
        new Rect(_offsetX, lane.Top, Left - 4, lane.Height),
        new Rect(_offsetX + 6, lane.Top + 4, Left - 16, 14),
        new Rect(_offsetX + 6, lane.Top + 20, Left - 16, 14))).ToArray();
    public event Action<long?>? SelectionChanged;

    public ExecutionTimelineSnapshot? Snapshot { get => GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, Math.Clamp(value, 1, 8)); }
    public int LanguageVersion { get => GetValue(LanguageVersionProperty); set => SetValue(LanguageVersionProperty, value); }

    static ExecutionTimelineChart() => AffectsRender<ExecutionTimelineChart>(SnapshotProperty, ZoomProperty, LanguageVersionProperty);

    public ExecutionTimelineChart()
    {
        Focusable = true;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        Rebuild();
    }

    public void SetViewport(double width, double height, double offsetX, double offsetY)
    {
        var changed = Math.Abs(_viewportWidth - width) > 1;
        _viewportWidth = width > 0 ? width : 900;
        _viewportHeight = height > 0 ? height : 600;
        _offsetX = Math.Max(0, offsetX);
        _offsetY = Math.Max(0, offsetY);
        if (changed) Rebuild();
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SnapshotProperty || change.Property == ZoomProperty) Rebuild();
    }

    private void Rebuild()
    {
        _bars.Clear();
        _lanes.Clear();
        var timelineWidth = Math.Max(320, _viewportWidth - Left - 24) * Zoom;
        Width = Left + timelineWidth + 24;
        var snapshot = Snapshot;
        var end = Math.Max(1, snapshot?.DurationUs ?? 1);
        var y = Header;
        foreach (var lane in new[] { ExecutionTimelineLane.Model, ExecutionTimelineLane.Tools, ExecutionTimelineLane.Script, ExecutionTimelineLane.PtcTools, ExecutionTimelineLane.System })
        {
            var spans = snapshot?.Spans.Where(span => span.Lane == lane).OrderBy(span => span.StartUs).ThenBy(span => span.Id).ToArray() ?? [];
            var active = new PriorityQueue<int, long>();
            var free = new SortedSet<int>();
            var tracks = 0;
            foreach (var span in spans)
            {
                while (active.TryPeek(out _, out var finish) && finish <= span.StartUs)
                    free.Add(active.Dequeue());
                var track = free.Count > 0 ? free.Min : tracks++;
                free.Remove(track);
                var final = Math.Max(span.StartUs, span.EndUs ?? snapshot!.DurationUs);
                active.Enqueue(track, Math.Max(final, span.StartUs + 1));
                var x = Left + timelineWidth * Math.Clamp((double)span.StartUs / end, 0, 1);
                var right = Left + timelineWidth * Math.Clamp((double)final / end, 0, 1);
                _bars.Add(new Bar(span, new Rect(x, y + track * RowHeight + 3, Math.Max(3, right - x), 20)));
            }
            // Every lane needs room for its title AND its localized explanation, including
            // an empty or single-track lane. Bars still use the existing compact track height.
            var height = Math.Max(MinLaneHeight, tracks * RowHeight);
            _lanes.Add((lane, y, height));
            y += height + GroupGap;
        }
        Height = Math.Max(Header + 5 * RowHeight, y + 8);
        InvalidateMeasure();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var ink = new SolidColorBrush(Color.Parse(dark ? "#E4E8EF" : "#293241"));
        var muted = new SolidColorBrush(Color.Parse(dark ? "#A4AEBD" : "#5E6B7D"));
        var rule = new Pen(new SolidColorBrush(Color.Parse(dark ? "#3D4858" : "#D9E0E8")), 1);
        var surface = new SolidColorBrush(Color.Parse(dark ? "#15151A" : "#FFFFFF"));
        var snapshot = Snapshot;
        var timelineWidth = Math.Max(1, Width - Left - 24);
        for (var i = 0; i <= 4; i++)
        {
            var x = Left + timelineWidth * i / 4;
            context.DrawLine(rule, new Point(x, Header - 8), new Point(x, Height));
            if (x >= _offsetX + Left - 1)
                DrawText(context, ExecutionTimelineViewModel.FormatDuration((snapshot?.DurationUs ?? 0) * i / 4), x + 3, 9, muted, 11);
        }
        foreach (var lane in _lanes)
        {
            if (lane.Top + lane.Height < _offsetY - 30 || lane.Top > _offsetY + _viewportHeight + 30) continue;
            context.DrawLine(rule, new Point(0, lane.Top + lane.Height + 5), new Point(Width, lane.Top + lane.Height + 5));
        }
        foreach (var bar in _bars)
        {
            var rect = bar.Bounds;
            if (rect.Bottom < _offsetY - 30 || rect.Top > _offsetY + _viewportHeight + 30
                || rect.Right < _offsetX - 30 || rect.Left > _offsetX + _viewportWidth + 30) continue;
            var color = bar.Span.Lane switch
            {
                ExecutionTimelineLane.Model => "#397DCA",
                ExecutionTimelineLane.Tools => "#168A82",
                ExecutionTimelineLane.Script => "#8252B3",
                ExecutionTimelineLane.PtcTools => "#2A9B71",
                _ => "#AC7629"
            };
            var failed = bar.Span.State is ExecutionTimelineState.Failed or ExecutionTimelineState.Denied
                or ExecutionTimelineState.Cancelled or ExecutionTimelineState.TimedOut or ExecutionTimelineState.OutcomeUnknown;
            var border = new Pen(new SolidColorBrush(Color.Parse(failed ? "#D95151" : _selected == bar.Span.Id ? "#F2BB3D" : color)), failed || _selected == bar.Span.Id ? 2 : 1);
            context.DrawRectangle(new SolidColorBrush(Color.Parse(color)), border, rect);
            if (rect.Width >= 55)
            {
                using (context.PushClip(rect))
                    DrawText(context, DataContext is ExecutionTimelineViewModel vm ? vm.NameFor(bar.Span)
                        : ExecutionTimelineViewModel.DisplayName(bar.Span), rect.X + 5, rect.Y + 3, Brushes.White, 11);
            }
        }
        if (_selected is { } selected)
        {
            var chosen = _bars.FirstOrDefault(bar => bar.Span.Id == selected);
            if (chosen is not null && chosen.Span.ParentId is { } parentId)
            {
                var parent = _bars.FirstOrDefault(bar => bar.Span.Id == parentId);
                if (parent is not null)
                    context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#F2BB3D")), 1.5),
                        new Point(parent.Span.Kind == ExecutionTimelineKind.ModelRequest ? parent.Bounds.Right : parent.Bounds.Left,
                            parent.Bounds.Bottom), new Point(chosen.Bounds.Left, chosen.Bounds.Top));
            }
        }
        // Freeze the lane labels while the time axis scrolls horizontally. Draw them last so
        // long bars cannot paint over their names; this remains one custom-rendered control.
        context.DrawRectangle(surface, null, new Rect(_offsetX, Header - 8, Left - 4, Math.Max(0, Height - Header + 8)));
        foreach (var label in LaneLabels)
        {
            if (label.LaneBounds.Bottom < _offsetY - 30 || label.LaneBounds.Top > _offsetY + _viewportHeight + 30) continue;
            DrawText(context, label.Title, label.TitleBounds.X, label.TitleBounds.Y, ink, 11);
            using (context.PushClip(label.DescriptionBounds))
                DrawText(context, label.Description, label.DescriptionBounds.X, label.DescriptionBounds.Y, muted, 10);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        var hit = point.X < _offsetX + Left ? null : _bars.LastOrDefault(bar => bar.Bounds.Inflate(2).Contains(point));
        _selected = hit?.Span.Id;
        if (hit is not null) Focus();
        SelectionChanged?.Invoke(_selected);
        InvalidateVisual();
        e.Handled = hit is not null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        var hit = point.X < _offsetX + Left ? null : _bars.LastOrDefault(bar => bar.Bounds.Inflate(2).Contains(point));
        if (_hovered == hit?.Span.Id) return;
        _hovered = hit?.Span.Id;
        var name = hit is null ? null : DataContext is ExecutionTimelineViewModel vm ? vm.NameFor(hit.Span)
            : ExecutionTimelineViewModel.DisplayName(hit.Span);
        ToolTip.SetTip(this, name);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var direction = e.Key is Key.Right or Key.Down ? 1 : e.Key is Key.Left or Key.Up ? -1 : 0;
        if (direction == 0 || _bars.Count == 0) return;
        var order = _bars.OrderBy(bar => bar.Span.StartUs).ThenBy(bar => bar.Span.Id).ToArray();
        var index = Array.FindIndex(order, bar => bar.Span.Id == _selected);
        index = index < 0 ? direction > 0 ? 0 : order.Length - 1 : Math.Clamp(index + direction, 0, order.Length - 1);
        _selected = order[index].Span.Id;
        SelectionChanged?.Invoke(_selected);
        InvalidateVisual();
        e.Handled = true;
    }

    private static void DrawText(DrawingContext context, string text, double x, double y, IBrush brush, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Inter"), size, brush);
        context.DrawText(formatted, new Point(x, y));
    }

    private static string LaneName(ExecutionTimelineLane lane) => lane switch
    {
        ExecutionTimelineLane.Model => "MODEL",
        ExecutionTimelineLane.Tools => "TOOLS",
        ExecutionTimelineLane.Script => "SCRIPT",
        ExecutionTimelineLane.PtcTools => "PTC TOOLS",
        _ => "SYSTEM"
    };
}
