using System;
using System.Threading.Tasks;
using Athena.UI.Controls;
using Athena.UI.Services.ExecutionTimeline;
using Athena.UI.Services.Interfaces;
using Athena.UI.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Serilog;

namespace Athena.UI.Views;

/// <summary>Non-modal so the user can still stop a live reply while inspecting it.</summary>
public partial class ExecutionTimelineWindow : Window, IDisposable
{
    private readonly ExecutionTimelineViewModel _viewModel;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _refreshing, _closed;

    public ExecutionTimelineWindow() : this(null, string.Empty, null) { }

    public ExecutionTimelineWindow(ExecutionTimelineService? service, string messageId, ILocalizationService? localization)
    {
        InitializeComponent();
        _viewModel = new ExecutionTimelineViewModel(service, messageId, localization);
        DataContext = _viewModel;
        this.FindControl<ExecutionTimelineChart>("TimelineChart")!.SelectionChanged += _viewModel.Select;
        _timer.Tick += OnTimerTick;
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try { await RefreshAsync(); }
        catch (Exception ex) { Log.Error(ex, "Could not load the execution timeline"); }
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        try { await RefreshAsync(); }
        catch (Exception ex) { _timer.Stop(); Log.Error(ex, "Could not refresh the live execution timeline"); }
    }

    private async Task RefreshAsync()
    {
        if (_closed || _refreshing) return;
        _refreshing = true;
        try
        {
            await _viewModel.RefreshAsync();
            if (_closed) return;
            UpdateViewport();
            if (_viewModel.IsLive) _timer.Start();
            else _timer.Stop();
        }
        finally { _refreshing = false; }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _viewModel.Dispose();
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e)
    {
        var chart = this.FindControl<ExecutionTimelineChart>("TimelineChart")!;
        chart.Zoom = Math.Min(8, chart.Zoom * 2);
    }
    private void OnZoomOut(object? sender, RoutedEventArgs e)
    {
        var chart = this.FindControl<ExecutionTimelineChart>("TimelineChart")!;
        chart.Zoom = Math.Max(1, chart.Zoom / 2);
    }
    private void OnFit(object? sender, RoutedEventArgs e) => this.FindControl<ExecutionTimelineChart>("TimelineChart")!.Zoom = 1;
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => UpdateViewport();

    private void UpdateViewport()
    {
        var scroll = this.FindControl<ScrollViewer>("TimelineScroll");
        var chart = this.FindControl<ExecutionTimelineChart>("TimelineChart");
        if (scroll is not null && chart is not null)
            chart.SetViewport(scroll.Viewport.Width, scroll.Viewport.Height, scroll.Offset.X, scroll.Offset.Y);
    }
}
