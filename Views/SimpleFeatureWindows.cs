using Avalonia.Controls;

namespace Athena.UI.Views;

public sealed class KnowledgeBaseWindow : Window
{
    public KnowledgeBaseWindow(object dataContext)
    {
        Title = "知识库";
        Width = 1080;
        Height = 760;
        MinWidth = 820;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new KnowledgeBaseTabView { DataContext = dataContext };
    }
}

public sealed class ScheduledMessagesWindow : Window
{
    public ScheduledMessagesWindow(object dataContext)
    {
        Title = "定时消息";
        Width = 980;
        Height = 720;
        MinWidth = 760;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new TasksTabView { DataContext = dataContext };
    }
}

public sealed class DetailedLogsWindow : Window
{
    public DetailedLogsWindow(object dataContext)
    {
        Title = "详细日志";
        Width = 1080;
        Height = 720;
        MinWidth = 800;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new LogsTabView { DataContext = dataContext };
    }
}
