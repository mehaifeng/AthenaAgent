using Athena.UI.Services.ComputerUse;
using Athena.UI.Services.Interfaces;
using Athena.UI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Views;

public sealed partial class ComputerUseWindow : Window, IDisposable
{
    private const string DemoText = "Athena 桌面操作演示";
    private readonly ComputerUseService _service;
    private readonly ComputerUseViewModel _viewModel;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly bool _demoOnly;
    private bool _closed;
    private const double FixtureWidth = 470;
    private Process? _fixture;
    private CancellationTokenSource? _demoCancellation;

    public ComputerUseWindow() : this(App.Services!.GetRequiredService<ComputerUseService>(),
        App.Services!.GetRequiredService<ILocalizationService>()) { }

    public ComputerUseWindow(ComputerUseService service, ILocalizationService localization, bool demoOnly = false)
    {
        _service = service;
        _demoOnly = demoOnly;
        _viewModel = new ComputerUseViewModel(service, localization);
        InitializeComponent();
        DataContext = _viewModel;
        Targets.IsVisible = !demoOnly;
        _service.ProgressChanged += OnProgress;
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_demoOnly)
            await PlayDemoAsync();
        else
            await RefreshAsync();
    }

    private async void OnRefresh(object? sender, RoutedEventArgs e) => await RefreshAsync();
    private async void OnDemo(object? sender, RoutedEventArgs e) => await PlayDemoAsync();
    private void OnStop(object? sender, RoutedEventArgs e)
    {
        _demoCancellation?.Cancel();
        _service.Stop();
    }

    private async Task RefreshAsync()
    {
        try { await _viewModel.RefreshAsync(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Closing the panel cancels its pending enumeration.
        }
    }

    private async Task PlayDemoAsync()
    {
        if (_viewModel.Busy || _closed)
            return;
        using var demo = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _demoCancellation = demo;
        _viewModel.Busy = true;
        Preview.Reset();
        _viewModel.HasFrame = false;
        CloseFixture();
        try
        {
            var layout = new TaskCompletionSource<(Point Field, Point Confirm)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var verified = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var position = PositionFixture();
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
            start.ArgumentList.Add("--computer-use-target");
            start.ArgumentList.Add(position.X.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add(position.Y.ToString(CultureInfo.InvariantCulture));
            var fixture = Process.Start(start) ?? throw new InvalidOperationException("Could not open the demo form.");
            _fixture = fixture;
            fixture.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not { } message)
                    return;
                if (message == $"ATHENA_CUA_CONFIRMED {DemoText}")
                    verified.TrySetResult(true);
                if (message.StartsWith("ATHENA_CUA_LAYOUT ", StringComparison.Ordinal))
                {
                    var values = message.Split(' ');
                    if (values.Length == 5
                        && TryParsePoint(values[1], values[2], out var input)
                        && TryParsePoint(values[3], values[4], out var button))
                        layout.TrySetResult((input, button));
                }
            };
            fixture.BeginOutputReadLine();
            var (field, confirm) = await layout.Task.WaitAsync(TimeSpan.FromSeconds(8), demo.Token);
            ComputerUseTarget? target = null;
            for (var attempt = 0; attempt < 15 && target is null; attempt++)
            {
                await Task.Delay(150, demo.Token);
                var windows = await _service.ListWindowsAsync(demo.Token, fixture.Id);
                target = windows.FirstOrDefault();
            }
            if (target is null)
                throw new InvalidOperationException(_viewModel.L("ComputerUse.DemoWindowMissing", "The driver did not find the demo window."));

            var result = await _service.RunAsync(target, (frame, _, step, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (step == 0)
                    return Task.FromResult(ActionForControl(frame, "AthenaDemoField", field, "type_text") with { Text = DemoText });
                if (step == 1)
                    return Task.FromResult(ActionForControl(frame, "AthenaDemoConfirm", confirm, "click"));

                var success = verified.Task.IsCompletedSuccessfully;
                return Task.FromResult(new ComputerUseAction("finish", Success: success, Summary: success
                    ? _viewModel.L("ComputerUse.DemoSuccess", "The target window confirmed the text entered by Cua Driver.")
                    : _viewModel.L("ComputerUse.DemoUnverified", "The target window did not confirm the entered text.")));
            }, demo.Token);
            Log.Information("Computer Use demo verified: {Success}", result.Success);
        }
        catch (OperationCanceledException) when (demo.IsCancellationRequested)
        {
            if (!_closed)
                _viewModel.Update(new(ComputerUsePhase.Stopped));
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Computer Use demo failed");
            _viewModel.Update(new(ComputerUsePhase.Failed, Detail: exception.Message));
        }
        finally
        {
            _demoCancellation = null;
            _viewModel.Busy = false;
        }
    }

    private static ComputerUseAction ActionForControl(ComputerUseFrame frame, string label, Point point, string action)
    {
        var control = frame.Elements.FirstOrDefault(element => element.Label == label);
        return new(action, point.X * frame.Width, point.Y * frame.Height, Element: control?.Index);
    }

    private PixelPoint PositionFixture()
    {
        var screen = Screens.ScreenFromWindow(this);
        if (screen is null)
            return Position;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        if (area.Width >= (Width + FixtureWidth + 70) * scale)
        {
            Position = new PixelPoint(area.X + (int)(24 * scale), Position.Y);
            return new PixelPoint(Position.X + (int)((Width + 18) * scale), Position.Y + (int)(30 * scale));
        }
        return new PixelPoint(area.Right - (int)((FixtureWidth + 20) * scale), area.Y + (int)(24 * scale));
    }

    private static bool TryParsePoint(string x, string y, out Point point)
    {
        const NumberStyles style = NumberStyles.Float;
        var validX = double.TryParse(x, style, CultureInfo.InvariantCulture, out var px);
        var validY = double.TryParse(y, style, CultureInfo.InvariantCulture, out var py);
        point = new(px, py);
        return validX && validY && px is > 0 and < 1 && py is > 0 and < 1;
    }

    private void CloseFixture()
    {
        using var fixture = _fixture;
        _fixture = null;
        if (fixture is null)
            return;
        try
        {
            if (!fixture.HasExited)
                fixture.Kill();
        }
        catch (InvalidOperationException exception)
        {
            Log.Debug(exception, "Demo form already exited");
        }
    }

    private void OnProgress(ComputerUseProgress progress) => Dispatcher.UIThread.Post(() =>
    {
        if (_closed)
            return;
        if (progress.Phase == ComputerUsePhase.Observing && progress.Frame is null)
        {
            Preview.Reset();
            _viewModel.HasFrame = false;
            _viewModel.Busy = true;
        }
        try
        {
            Preview.Show(progress);
            _viewModel.Update(progress);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not render the Computer Use frame");
            _viewModel.Update(new(ComputerUsePhase.Failed, Detail: exception.Message));
            _service.Stop();
        }
        if (progress.Phase is ComputerUsePhase.Completed or ComputerUsePhase.Failed or ComputerUsePhase.Stopped)
            _viewModel.Busy = false;
    });

    private void OnClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_closed)
            return;
        _closed = true;
        _service.ProgressChanged -= OnProgress;
        _service.SelectedTarget = null;
        _service.Stop();
        _lifetime.Cancel();
        CloseFixture();
        _lifetime.Dispose();
        Preview.Dispose();
    }
}
