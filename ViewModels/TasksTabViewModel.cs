using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Athena.UI.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public partial class TasksTabViewModel : ViewModelBase
{
    private readonly ITaskScheduler? _taskScheduler;
    private readonly ObservableCollection<ScheduledTask> _localTasks = new();

    public ObservableCollection<ScheduledTask> ScheduledTasks => _taskScheduler?.Tasks ?? _localTasks;

    public TasksTabViewModel() : this(null) { }

    public TasksTabViewModel(ITaskScheduler? taskScheduler)
    {
        _taskScheduler = taskScheduler;
    }

    [RelayCommand]
    private async Task CreateTaskAsync()
    {
        var viewModel = new CreateTaskDialogViewModel();
        var dialog = new CreateTaskDialog(viewModel);

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var activeWindow = desktop.MainWindow;
            if (activeWindow != null) await dialog.ShowDialog(activeWindow);
            else dialog.Show();
        }
        else dialog.Show();

        if (viewModel.IsConfirmed && viewModel.Result != null)
        {
            if (_taskScheduler != null) await _taskScheduler.ScheduleAsync(viewModel.Result);
            else _localTasks.Add(viewModel.Result);
        }
    }

    [RelayCommand]
    private async Task ClearAllTasksAsync()
    {
        if (_taskScheduler != null) await _taskScheduler.ClearAllAsync();
        else _localTasks.Clear();
    }
}
