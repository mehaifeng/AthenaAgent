using Athena.UI;
using Athena.UI.Markup;
using Athena.UI.Services.Interfaces;
using Athena.UI.ViewModels;
using Athena.UI.Views;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;

internal static class LocalizationBindingTests
{
    internal static void Run()
    {
        var localization = App.Services!.GetRequiredService<ILocalizationService>();
        var initialLanguage = localization.CurrentLanguage;
        using var knowledge = new KnowledgeBaseViewModel();
        using var tasks = new TasksViewModel();
        using var logs = new LogsViewModel();
        var windows = new Window[]
        {
            new KnowledgeBaseWindow(knowledge, localization),
            new TasksWindow(tasks, localization),
            new DetailedLogsWindow(logs, localization)
        };
        var label = new TextBlock();
        using var labelBinding = label.Bind(TextBlock.TextProperty,
            (Binding)new LocExtension("Window.Tasks.Title").ProvideValue(null!));
        var fallbackLabel = new TextBlock();
        using var fallbackBinding = fallbackLabel.Bind(TextBlock.TextProperty,
            (Binding)new LocExtension("Test.MissingLocalization") { DefaultValue = "Fallback" }.ProvideValue(null!));

        try
        {
            foreach (var window in windows) window.Show();

            localization.SwitchLanguage("en-US");
            Dispatcher.UIThread.RunJobs();
            Require(windows.Select(window => window.Title).SequenceEqual(["Knowledge base", "Scheduled tasks", "Detailed logs"])
                && label.Text == "Scheduled tasks" && fallbackLabel.Text == "Fallback",
                "英语切换应即时更新已打开的窗口和标记扩展绑定，并保留缺失译文的默认值。");

            localization.SwitchLanguage("zh-CN");
            Dispatcher.UIThread.RunJobs();
            Require(windows.Select(window => window.Title).SequenceEqual(["知识库", "定时消息", "详细日志"])
                && label.Text == "定时消息" && fallbackLabel.Text == "Fallback",
                "中文切换应复用同一批绑定。");

            foreach (var window in windows) window.Close();
            var closedTitles = windows.Select(window => window.Title).ToArray();
            localization.SwitchLanguage("en-US");
            Dispatcher.UIThread.RunJobs();
            Require(windows.Select(window => window.Title).SequenceEqual(closedTitles),
                "关闭的窗口必须释放标题绑定。");
        }
        finally
        {
            foreach (var window in windows)
            {
                if (window.IsVisible) window.Close();
            }

            localization.SwitchLanguage(initialLanguage);
            Dispatcher.UIThread.RunJobs();
        }

        Console.WriteLine("[PASS] localization bindings refresh active controls and window titles, retain fallbacks and release closed title bindings");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
