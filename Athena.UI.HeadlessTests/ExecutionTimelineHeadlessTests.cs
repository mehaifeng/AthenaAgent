using System.Diagnostics;
using Athena.UI.Controls;
using Athena.UI.Models;
using Athena.UI.Services.ExecutionTimeline;
using Athena.UI.Services;
using Athena.UI.ViewModels;
using Athena.UI.Views;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Serilog;

internal static class ExecutionTimelineHeadlessTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "athena-timeline-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new AppConfig { ExecutionAnalysisEnabled = true };
            var localization = new LocalizationService();
            localization.SwitchLanguage("zh-CN");
            var configService = new HeadlessConfigService(config);
            using var timelines = new ExecutionTimelineService(configService, new TemporaryPathService(root), Log.Logger);
            using var chat = new MainConversationViewModel(null, configService, null, null, null, localization, executionTimelines: timelines);
            var user = new ChatMessage { Role = "user", Content = "test" };
            var assistant = new ChatMessage { Role = "assistant", Content = "reply", IsLoading = true, IsStreaming = true };
            var hidden = new ChatMessage { Role = "assistant", IsHidden = true, Content = "tool carrier" };
            chat.Messages.Add(user);
            chat.Messages.Add(assistant);
            chat.Messages.Add(hidden);
            Check(user.ExecutionTimelineButtonContent is null && hidden.ExecutionTimelineButtonContent is null, "only visible assistant bubbles receive an action");
            Check(assistant.ExecutionTimelineButtonContent == assistant && assistant.ShowBubbleActions, "the action exists while the reply is loading");

            var origin = Stopwatch.GetTimestamp() - 5 * Stopwatch.Frequency;
            long At(double seconds) => origin + (long)(Stopwatch.Frequency * seconds);
            var recorder = timelines.TryBegin(assistant.Id, origin, DateTimeOffset.UtcNow)!;
            var model = recorder.Begin(ExecutionTimelineLane.Model, ExecutionTimelineKind.ModelRequest, "model", round: 1, attempt: 1, timestamp: At(0.1));
            recorder.Usage(model, 100, 5, 20);
            recorder.End(model, ExecutionTimelineState.Succeeded, At(1));
            var parent = recorder.QueueOuterTool("outer", "execute_program", true, 1, At(1.02), model);
            recorder.StartOuterTool(parent, At(1.04));
            recorder.ObservePtc(new("run", "outer", "c", 1, "ScriptStarted", null, null, null) { OccurredTimestamp = At(1.1) });
            recorder.ObservePtc(new("run", "outer", "c", 2, "CallQueued", "a", "read_system_file", null) { OccurredTimestamp = At(1.2) });
            recorder.ObservePtc(new("run", "outer", "c", 3, "CallStarted", "a", "read_system_file", null) { OccurredTimestamp = At(1.3), CallState = "Running" });
            recorder.ObservePtc(new("run", "outer", "c", 4, "CallQueued", "b", "get_file_info", null) { OccurredTimestamp = At(1.4) });
            recorder.ObservePtc(new("run", "outer", "c", 5, "CallStarted", "b", "get_file_info", null) { OccurredTimestamp = At(1.5), CallState = "Running" });
            recorder.ObservePtc(new("run", "outer", "c", 6, "CallFinished", "a", "read_system_file", null) { OccurredTimestamp = At(2), CallState = "Succeeded" });
            recorder.ObservePtc(new("run", "outer", "c", 7, "CallFinished", "b", "get_file_info", null) { OccurredTimestamp = At(2.3), CallState = "Succeeded" });
            recorder.ObservePtc(new("run", "outer", "c", 8, "ScriptFinished", null, null, null) { OccurredTimestamp = At(2.5), RunStatus = "Completed" });
            recorder.ObservePtc(new("run", "outer", "c", 9, "RunFinished", null, null, null) { OccurredTimestamp = At(2.6), RunStatus = "Completed" });
            recorder.End(parent, ExecutionTimelineState.Succeeded, At(2.7));

            var view = new MainConversationView { DataContext = chat };
            var shell = new Window { Content = view, Width = 1000, Height = 700 };
            shell.Show();
            Dispatcher.UIThread.RunJobs();
            var buttons = view.GetVisualDescendants().OfType<Button>().Where(item => item.Name == "ExecutionTimelineButton").ToArray();
            Check(buttons.Length == 1, "exactly one lazily realized timeline button per visible assistant bubble");
            buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var window = shell.OwnedWindows.OfType<ExecutionTimelineWindow>().Single();
            var chart = window.FindControl<ExecutionTimelineChart>("TimelineChart")!;
            var vm = (ExecutionTimelineViewModel)window.DataContext!;
            var labels = chart.LaneLabels;
            Check(labels.Count == 5, "every timeline lane has one y-axis title and explanation");
            var expectedLabels = new[] { ("MODEL", "主模型"), ("TOOLS", "外层工具"), ("SCRIPT", "脚本生命周期"), ("PTC TOOLS", "PTC 子工具"), ("SYSTEM", "等待/审批") };
            Check(labels.Select(label => (label.Title, label.Description)).SequenceEqual(expectedLabels), "Chinese explanations are under their corresponding lane names");
            Check(labels.All(label => label.TitleBounds.Bottom <= label.DescriptionBounds.Top
                    && label.DescriptionBounds.Bottom <= label.LaneBounds.Bottom
                    && label.DescriptionBounds.Right <= label.LaneBounds.Right),
                "lane explanations fit underneath the title and remain inside the frozen y-axis");
            Check(!window.GetVisualDescendants().OfType<TextBlock>().Any(item => item.Text?.Contains("MODEL 主模型", StringComparison.Ordinal) == true),
                "the old combined legend is no longer rendered above the chart");
            var languageVersion = chart.LanguageVersion;
            try
            {
                localization.SwitchLanguage("en-US");
                Dispatcher.UIThread.RunJobs();
                Check(chart.LanguageVersion > languageVersion && chart.LaneLabels[0].Description == "main model"
                    && chart.LaneLabels[2].Description == "script lifetime", "a completed or live chart invalidates its y-axis when language changes");
            }
            finally
            {
                localization.SwitchLanguage("zh-CN"); // Localization dictionaries are global; restore the suite's pinned culture.
                Dispatcher.UIThread.RunJobs();
            }
            Check(vm.IsLive && chart.Bars.Any(bar => bar.Span.Lane == ExecutionTimelineLane.Model), "click opens a non-modal live chart with the model lane");
            Check(vm.Summary.Contains("模型", StringComparison.Ordinal), "timeline summary follows the application's Chinese language");
            Check(chart.Bars.Any(bar => bar.Span.Lane == ExecutionTimelineLane.Tools) && chart.Bars.Any(bar => bar.Span.Lane == ExecutionTimelineLane.Script), "outer tool and script lanes are distinct");
            var a = chart.Bars.Single(bar => bar.Span.Name == "read_system_file");
            var b = chart.Bars.Single(bar => bar.Span.Name == "get_file_info");
            Check(a.Bounds.Right > b.Bounds.Left && a.Bounds.Y != b.Bounds.Y, "overlapping PTC tools occupy different visible subrows");
            vm.Select(a.Span.Id);
            Check(vm.Detail.Contains("read_system_file", StringComparison.Ordinal) && vm.Detail.Contains("execute_program", StringComparison.Ordinal), "selection exposes the parent relationship");
            chart.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
            Check(vm.Detail.Contains("MODEL", StringComparison.Ordinal), "keyboard navigation selects a timeline bar and updates accessible details");
            window.FindControl<Button>("TimelineZoomIn")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(chart.Zoom == 2, "zoom button operates on the real chart");
            chart.SetViewport(900, 500, 140, 0);
            Check(chart.LaneLabels.All(label => Math.Abs(label.TitleBounds.X - 140 - 6) < 0.1
                    && Math.Abs(label.DescriptionBounds.X - 140 - 6) < 0.1),
                "both lines of the y-axis remain pinned during horizontal scrolling");
            chart.SetViewport(900, 500, 0, 0);
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Timeline window did not render.");
            Check(frame.PixelSize.Width > 0 && frame.PixelSize.Height > 0, "timeline window renders in Avalonia headless");
            var capture = Environment.GetEnvironmentVariable("ATHENA_TIMELINE_CAPTURE");
            if (!string.IsNullOrWhiteSpace(capture))
            {
                using var output = File.Create(capture);
                frame.Save(output);
            }
            window.RequestedThemeVariant = ThemeVariant.Dark;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Check(chart.ActualThemeVariant == ThemeVariant.Dark, "timeline chart follows the dark theme");
            using (var darkFrame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Dark timeline window did not render."))
            {
                var darkCapture = Environment.GetEnvironmentVariable("ATHENA_TIMELINE_DARK_CAPTURE");
                if (!string.IsNullOrWhiteSpace(darkCapture))
                {
                    using var output = File.Create(darkCapture);
                    darkFrame.Save(output);
                }
            }

            using var configurationSession = new AppConfigurationSession(configService);
            using var settingsState = new AppSettingsState(configurationSession);
            var settingsView = new AgentRuntimeSettingsView { DataContext = new AgentRuntimeSettingsViewModel(settingsState) };
            var settingsWindow = new Window { Content = settingsView, Width = 900, Height = 700 };
            settingsWindow.Show();
            Dispatcher.UIThread.RunJobs();
            var toggle = settingsView.FindControl<CheckBox>("ExecutionAnalysisEnabledToggle")!;
            Check(toggle.IsChecked == true, "settings page reflects the actual enabled config");
            toggle.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Check(!config.ExecutionAnalysisEnabled && assistant.ExecutionTimelineButtonContent is null && recorder.Snapshot().IsPartial,
                "the real settings toggle hides the action and stops capture immediately");
            settingsWindow.Close();
            window.Close();
            shell.Close();
            var completed = timelines.CompleteAsync(recorder, ExecutionTimelineState.Succeeded);
            PumpUntil(() => completed.IsCompleted, "timeline store did not complete");
            completed.GetAwaiter().GetResult(); // Finished task only; no dispatcher wait.

            using var missing = new ExecutionTimelineWindow(timelines, Guid.NewGuid().ToString("N"), null);
            missing.Show();
            Dispatcher.UIThread.RunJobs();
            var missingVm = (ExecutionTimelineViewModel)missing.DataContext!;
            PumpUntil(() => !string.IsNullOrEmpty(missingVm.Notice) && !missingVm.Notice.Contains("Loading", StringComparison.Ordinal), "unrecorded history did not reach its empty state");
            Check(missingVm.Snapshot is null && !string.IsNullOrWhiteSpace(missingVm.Notice), "old bubbles show an explicit empty state");
            missing.Close();
            localization.SwitchLanguage("en-US");
            localization.SwitchLanguage("zh-CN"); // Localization dictionaries are global: restore the suite's pinned culture.
        }
        finally
        {
            // Only a fresh GUID-named test directory; never the user's application data.
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("[PASS] execution timeline bubble action, live window, five lanes, parallel geometry, selection, zoom, stop and legacy empty state");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Execution timeline UI: " + message);
    }

    private static void PumpUntil(Func<bool> done, string failure)
    {
        var until = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
        while (!done())
        {
            if (Stopwatch.GetTimestamp() > until) throw new InvalidOperationException(failure);
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
    }
}
