using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using Athena.UI.Models;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Athena.UI.Controls;

/// <summary>Pixel scrolling over mixed-height bubbles. Each visited row keeps its own measured
/// height, so a tall diagram never changes the estimated height of every other message.</summary>
public sealed class MessageViewportPanel : VirtualizingPanel
{
    private sealed class Row(ChatMessage message)
    {
        public ChatMessage Message { get; } = message;
        public double Height { get; set; } = message.IsUser ? 112 : 260;
        public double Top { get; set; }
        public int Index { get; set; }
        public Control? Container { get; set; }
        public long LastUsed { get; set; }
    }

    private readonly List<Row> _rows = [];
    private readonly List<Row> _realized = [];
    private IScrollAnchorProvider? _anchors;
    private Rect _viewport;
    private double _extent;
    private double _bufferTop;
    private double _bufferBottom;
    private double _measuredWidth = double.NaN;
    private long _lastWidthChange;
    private bool _itemsChanged = true;
    private const int RetainedRows = 36;
    private long _layoutPass;
    private bool _preparing;
    private int _first, _last;
    private double _direction = 1;

    public MessageViewportPanel() => EffectiveViewportChanged += ViewportChanged;

    private void ViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        var viewport = e.EffectiveViewport;
        if (viewport.Y != _viewport.Y) _direction = Math.Sign(viewport.Y - _viewport.Y);
        _viewport = viewport;
        // Leave at least a quarter-screen for the next wheel event. Ordinary pixel scrolling
        // within the prepared range only moves the existing composition visuals.
        if (_viewport.Y - _viewport.Height * .25 < _bufferTop && _bufferTop > 0
            || _viewport.Bottom + _viewport.Height * .25 > _bufferBottom && _bufferBottom < _extent
            || _realized.Count == 0 && _rows.Count != 0)
            InvalidateMeasure();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _anchors = this.FindAncestorOfType<IScrollAnchorProvider>();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        foreach (var row in _realized) _anchors?.UnregisterAnchorCandidate(row.Container!);
        _anchors = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        if (items.Count == 0)
        {
            // A hidden presenter need never be measured again. Release its bindings and
            // attachment leases as soon as the old source goes away.
            while (_realized.Count > 0) Release(_realized[^1]);
            _rows.Clear();
            _extent = _bufferTop = _bufferBottom = 0;
            _itemsChanged = false;
        }
        else if (!_itemsChanged && e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex == _rows.Count)
        {
            var first = _rows.Count;
            for (var i = first; i < items.Count; i++)
                _rows.Add(new Row((ChatMessage)items[i]!) { Index = i });
            UpdatePositions(first);
        }
        else _itemsChanged = true;
        InvalidateMeasure();
    }

    private void UpdateRows()
    {
        var previous = _rows.ToDictionary(row => row.Message);
        _rows.Clear();
        foreach (var message in Items.Cast<ChatMessage>())
        {
            if (!previous.Remove(message, out var row)) row = new Row(message);
            var index = _rows.Count;
            if (row.Container != null && index != row.Index)
                ItemContainerGenerator!.ItemContainerIndexChanged(row.Container, row.Index, index);
            row.Index = index;
            _rows.Add(row);
        }
        foreach (var removed in previous.Values)
            if (removed.Container != null) Release(removed);
        for (var i = 1; i < _realized.Count; i++)
        {
            if (_realized[i - 1].Index <= _realized[i].Index) continue;
            var row = _realized[i];
            var position = _realized.FindIndex(existing => existing.Index > row.Index);
            Children.Move(i, position);
            _realized.RemoveAt(i);
            _realized.Insert(position, row);
        }
        UpdatePositions(0);
        _itemsChanged = false;
    }

    private void UpdatePositions(int first)
    {
        double next = first == 0 ? 0 : _rows[first - 1].Top + _rows[first - 1].Height;
        for (var i = first; i < _rows.Count; i++)
        {
            var row = _rows[i];
            row.Top = next;
            next += row.Height;
        }
        _extent = next;
    }

    private int RowAt(double offset)
    {
        int lo = 0, hi = _rows.Count - 1;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_rows[mid].Top + _rows[mid].Height <= offset) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_itemsChanged) UpdateRows();
        if (_rows.Count == 0) return default;
        _layoutPass++;
        var height = Math.Max(1, _viewport.Height);
        var y = Math.Clamp(_viewport.Y, 0, Math.Max(0, _extent - height));
        if (double.IsFinite(_measuredWidth) && availableSize.Width != _measuredWidth)
            _lastWidthChange = Stopwatch.GetTimestamp();
        _measuredWidth = availableSize.Width;
        // All visible rows reflow at the current width on every drag. Layout of offscreen
        // Markdown can wait until scrolling reaches it.
        var overscan = IsResizing ? 0 : height * .6;

        // Grow outward from the visible row. If measuring a short row exposes another one,
        // cover the newly exposed area in this same layout pass.
        var anchor = RowAt(y);
        var top = _rows[anchor].Top;
        var bottom = top;
        var changed = _rows.Count;
        double width = 0;
        void Measure(int index)
        {
            var row = _rows[index];
            var container = Realize(row);
            row.LastUsed = _layoutPass;
            container.Measure(availableSize);
            width = Math.Max(width, container.DesiredSize.Width);
            var measured = container.DesiredSize.Height;
            if (row.Height != measured)
            {
                row.Height = measured;
                changed = Math.Min(changed, index);
            }
        }
        var last = anchor;
        do
        {
            Measure(last);
            bottom += _rows[last].Height;
            if (bottom >= y + height + overscan || last == _rows.Count - 1) break;
            last++;
        }
        while (true);
        var first = anchor;
        while (top > y - overscan && first > 0)
        {
            Measure(--first);
            top -= _rows[first].Height;
        }
        _first = first;
        _last = last;
        // Recently visited neighbours keep their Markdown document and text layout when
        // reversing direction. A width change only measures the active band.
        while (_realized.Count > RetainedRows)
        {
            var oldest = _realized.Where(row => row.LastUsed != _layoutPass && !row.Container!.IsKeyboardFocusWithin)
                .MinBy(row => row.LastUsed);
            if (oldest == null) break;
            Release(oldest);
        }
        if (changed < _rows.Count) UpdatePositions(changed);
        _bufferTop = _rows[first].Top;
        _bufferBottom = _rows[last].Top + _rows[last].Height;
        PrepareNeighbours();
        return new Size(width, _extent);
    }

    private bool IsResizing => _lastWidthChange != 0
        && Stopwatch.GetElapsedTime(_lastWidthChange).TotalMilliseconds < 100;

    // Build at most one neighbouring item per frame. Its native Markdown renderer starts
    // its parse on arrange. Input can run between items, even after a large scrollbar jump.
    private void PrepareNeighbours()
    {
        if (_preparing || IsResizing || _rows.Count == 0 || _viewport.Height <= 0 || TopLevel.GetTopLevel(this) is not { } window) return;
        const int ahead = 12, behind = 5;
        int Find(int start, int direction, int limit, double distance)
        {
            for (var step = 1; step <= limit && distance > 0; step++)
            {
                var i = start + step * direction;
                if ((uint)i >= (uint)_rows.Count) break;
                if (_rows[i].Container is not { IsMeasureValid: true, IsArrangeValid: true } ready) return i;
                // One long answer can cover more than ten screens. Its actual height is
                // available even before it enters the active band and changes the extent.
                distance -= ready.DesiredSize.Height;
            }
            return -1;
        }
        var down = _direction > 0;
        var forward = _viewport.Height * 2.5;
        var reverse = _viewport.Height;
        var index = down ? Find(_last, 1, ahead, forward) : Find(_first, -1, ahead, forward);
        if (index < 0) index = down ? Find(_first, -1, behind, reverse) : Find(_last, 1, behind, reverse);
        if (index < 0) return;
        var row = _rows[index];
        _preparing = true;
        window.RequestAnimationFrame(_ => Dispatcher.UIThread.Post(() =>
        {
            _preparing = false;
            if (_itemsChanged || IsResizing || (uint)index >= (uint)_rows.Count || _rows[index] != row
                || index >= _first && index <= _last || !this.IsAttachedToVisualTree() || !IsEffectivelyVisible) return;
            if (row.Container == null && _realized.Count >= RetainedRows)
            {
                // After a large jump, cached rows from the old location must not exhaust
                // the new neighbourhood's budget. Keep the active band and keyboard focus.
                var farthest = _realized.Where(r => (r.Index < _first - (down ? behind : ahead)
                    || r.Index > _last + (down ? ahead : behind)) && !r.Container!.IsKeyboardFocusWithin)
                    .MaxBy(r => Math.Max(_first - r.Index, r.Index - _last));
                if (farthest == null) return;
                Release(farthest);
            }
            var control = Realize(row);
            row.LastUsed = _layoutPass;
            SetRowActive(control, false);
            control.Measure(new Size(Bounds.Width, double.PositiveInfinity));
            control.Arrange(new Rect(0, row.Top, Bounds.Width, control.DesiredSize.Height));
            PrepareNeighbours();
        }, DispatcherPriority.Background));
    }

    private Control Realize(Row row)
    {
        if (row.Container != null) return row.Container;
        var generator = ItemContainerGenerator!;
        generator.NeedsContainer(row.Message, row.Index, out var key);
        var container = generator.CreateContainer(row.Message, row.Index, key);
        generator.PrepareItemContainer(container, row.Message, row.Index);
        var position = _realized.FindIndex(existing => existing.Index > row.Index);
        if (position < 0) position = _realized.Count;
        InsertInternalChild(position, container);
        row.Container = container;
        _realized.Insert(position, row);
        generator.ItemContainerPrepared(container, row.Message, row.Index);
        return container;
    }

    private static void SetRowActive(Control control, bool active)
    {
        // A pre-arranged Markdown document can exceed the row's estimated height. Keep
        // those speculative bounds out of painting and hit testing until measured in-band.
        var opacity = active ? 1d : 0d;
        if (control.Opacity != opacity) control.Opacity = opacity;
        if (control.IsHitTestVisible != active) control.IsHitTestVisible = active;
    }

    private void Release(Row row)
    {
        var control = row.Container!;
        _anchors?.UnregisterAnchorCandidate(control);
        RemoveInternalChild(control);
        ItemContainerGenerator!.ClearItemContainer(control);
        row.Container = null;
        _realized.Remove(row);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var row in _realized)
        {
            var control = row.Container!;
            var bounds = new Rect(0, row.Top, finalSize.Width, row.Height);
            var active = row.Index >= _first && row.Index <= _last || control.IsKeyboardFocusWithin;
            SetRowActive(control, active);
            if (active)
                control.Arrange(bounds);
            if (active && _viewport.Intersects(bounds)) _anchors?.RegisterAnchorCandidate(control);
            else _anchors?.UnregisterAnchorCandidate(control);
        }
        return finalSize;
    }

    protected override Control? ContainerFromIndex(int index) =>
        (uint)index < (uint)_rows.Count ? _rows[index].Container : null;

    protected override int IndexFromContainer(Control container) =>
        _realized.Find(row => ReferenceEquals(row.Container, container))?.Index ?? -1;

    protected override IEnumerable<Control> GetRealizedContainers() => _realized.Select(row => row.Container!);

    protected override Control? ScrollIntoView(int index)
    {
        if (_itemsChanged) UpdateRows();
        if ((uint)index >= (uint)_rows.Count) return null;
        var row = _rows[index];
        var control = Realize(row);
        SetRowActive(control, true);
        control.Measure(new Size(Bounds.Width, double.PositiveInfinity));
        if (row.Height != control.DesiredSize.Height)
        {
            row.Height = control.DesiredSize.Height;
            UpdatePositions(index);
        }
        control.Arrange(new Rect(0, row.Top, Bounds.Width, row.Height));
        InvalidateMeasure();
        control.BringIntoView();
        // Measuring the new neighbourhood can change the target's pixel position. Let
        // the ScrollViewer commit that extent, then align the measured row a second time.
        TopLevel.GetTopLevel(this)?.UpdateLayout();
        control.BringIntoView();
        return control;
    }

    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        if (_itemsChanged) UpdateRows();
        if (_rows.Count == 0) return null;
        var control = from as Control;
        var index = -1;
        while (control != null && control != this && (index = IndexFromContainer(control)) < 0)
            control = control.GetVisualParent() as Control;
        var target = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => _rows.Count - 1,
            NavigationDirection.Next or NavigationDirection.Down => index + 1,
            NavigationDirection.Previous or NavigationDirection.Up => index < 0 ? _rows.Count - 1 : index - 1,
            _ => int.MinValue
        };
        if (target == int.MinValue) return null;
        return ScrollIntoView(wrap ? (target + _rows.Count) % _rows.Count : target);
    }
}
