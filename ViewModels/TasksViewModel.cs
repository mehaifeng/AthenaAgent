using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Ursa.Controls;

namespace Athena.UI.ViewModels;

public partial class TasksViewModel : ViewModelBase
{
    private readonly ITaskScheduler? _taskScheduler;
    private readonly ILocalizationService? _localizationService;
    private readonly ObservableCollection<ScheduledTask> _localTasks = new();

    public ObservableCollection<ScheduledTask> ScheduledTasks => _taskScheduler?.Tasks ?? _localTasks;

    [ObservableProperty]
    private CreateTaskDialogViewModel _taskDraft;

    public TasksViewModel() : this(null, null) { }

    public TasksViewModel(ITaskScheduler? taskScheduler, ILocalizationService? localizationService = null)
    {
        _taskScheduler = taskScheduler;
        _localizationService = localizationService;
        _taskDraft = CreateTaskDraft();
    }

    [RelayCommand]
    private async Task CreateTaskAsync()
    {
        if (!TaskDraft.TryCreateResult(out var task) || task == null)
        {
            return;
        }

        if (_taskScheduler != null) await _taskScheduler.ScheduleAsync(task);
        else _localTasks.Add(task);

        TaskDraft = CreateTaskDraft();
    }

    [RelayCommand]
    private async Task ClearAllTasksAsync()
    {
        var result = await MessageBox.ShowAsync(
            message: _localizationService?.GetString("Dialog.ConfirmPurgeTasks") ?? "Are you sure you want to delete all scheduled tasks?",
            title: _localizationService?.GetString("Dialog.Title.Warning") ?? "Warning",
            button: MessageBoxButton.OKCancel,
            icon: MessageBoxIcon.Warning);

        if (result == MessageBoxResult.OK)
        {
            if (_taskScheduler != null) await _taskScheduler.ClearAllAsync();
            else _localTasks.Clear();
        }
    }

    [RelayCommand]
    private async Task DeleteTaskAsync(ScheduledTask task)
    {
        if (task == null) return;

        if (_taskScheduler != null)
        {
            await _taskScheduler.CancelAsync(task.Id);
        }
        else
        {
            _localTasks.Remove(task);
        }
    }

    private CreateTaskDialogViewModel CreateTaskDraft()
        => new(_localizationService);
}
