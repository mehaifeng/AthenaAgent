using Athena.UI.Models;
using Athena.UI.Services.Cron;
using Athena.UI.Services.Interfaces;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Ursa.Controls;

namespace Athena.UI.ViewModels;

/// <summary>
/// cron 任务页。
///
/// 职责被刻意限死在三件事：把服务的不可变快照投影成列表、把 CRUD/启停/立即运行转发给服务、
/// 把"打开这次运行的会话"交给导航边界。它不持有调度状态，也不碰主窗口的会话集合。
/// </summary>
public partial class TasksViewModel : ViewModelBase, IDisposable
{
    private readonly ICronTaskService? _taskService;
    private readonly ICronScheduleService _scheduleService;
    private readonly CronExecutionWorker? _executionWorker;
    private readonly IConversationNavigator? _navigator;
    private readonly IWorkspaceService? _workspaceService;
    private readonly ILocalizationService? _localizationService;
    private bool _disposed;

    public ObservableCollection<CronTaskItemViewModel> Tasks { get; } = new();

    [ObservableProperty]
    private CronTaskEditorViewModel _editor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedTask))]
    private CronTaskItemViewModel? _selectedTask;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private int _corruptedRecordCount;

    public bool HasSelectedTask => SelectedTask != null;

    public bool HasCorruptedRecords => CorruptedRecordCount > 0;

    public bool HasTasks => Tasks.Count > 0;

    public string CorruptedRecordsMessage => string.Format(
        L("Cron.Store.CorruptedRecords", "{0} stored task record(s) could not be read and were skipped."),
        CorruptedRecordCount);

    public TasksViewModel() : this(null, new CronScheduleService(), null, null, null, null) { }

    public TasksViewModel(
        ICronTaskService? taskService,
        ICronScheduleService scheduleService,
        CronExecutionWorker? executionWorker,
        IConversationNavigator? navigator,
        IWorkspaceService? workspaceService,
        ILocalizationService? localizationService)
    {
        _taskService = taskService;
        _scheduleService = scheduleService;
        _executionWorker = executionWorker;
        _navigator = navigator;
        _workspaceService = workspaceService;
        _localizationService = localizationService;

        _editor = new CronTaskEditorViewModel(_scheduleService, _localizationService);

        if (_taskService != null)
        {
            CorruptedRecordCount = _taskService.CorruptedRecordCount;
            _taskService.TasksChanged += OnTasksChanged;
            RebuildProjection(_taskService.GetTasks());
        }

        if (_localizationService != null)
        {
            _localizationService.LanguageChanged += OnLanguageChanged;
        }

        _ = LoadWorkspacesAsync();
    }

    private async Task LoadWorkspacesAsync()
    {
        if (_workspaceService == null) return;
        try
        {
            var workspaces = await _workspaceService.LoadAllAsync();
            RunOnUiThread(() => Editor.SetAvailableWorkspaces(workspaces));
        }
        catch (Exception)
        {
            // 工作区读不出来不该让任务页整页不可用：任务照常可建，只是落到全局分组。
        }
    }

    private void OnTasksChanged(object? sender, CronTaskListChangedEventArgs e)
        => RunOnUiThread(() => RebuildProjection(e.Tasks));

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_taskService != null) RebuildProjection(_taskService.GetTasks());
        OnPropertyChanged(nameof(CorruptedRecordsMessage));
    }

    /// <summary>
    /// 用不可变快照整体重建投影。逐条差量更新看起来更"高效"，但任务列表规模很小，
    /// 而差量更新是这类界面最常见的错位来源，不值得。
    /// </summary>
    private void RebuildProjection(IReadOnlyList<CronTask> tasks)
    {
        var previousSelectionId = SelectedTask?.Id;

        Tasks.Clear();
        foreach (var task in tasks.OrderByDescending(task => task.IsEnabled).ThenBy(task => task.NextOccurrence ?? DateTimeOffset.MaxValue))
        {
            var item = new CronTaskItemViewModel(task, _scheduleService, _localizationService);
            // 卡片上的动作通过事件回到这里，而不是让 XAML 在 item 模板里反查父级视图模型。
            item.EditRequested += (sender, _) => EditTaskCommand.Execute((CronTaskItemViewModel)sender!);
            item.ToggleEnabledRequested += (sender, _) => ToggleTaskEnabledCommand.Execute((CronTaskItemViewModel)sender!);
            item.RunNowRequested += (sender, _) => RunTaskNowCommand.Execute((CronTaskItemViewModel)sender!);
            item.DeleteRequested += (sender, _) => DeleteTaskCommand.Execute((CronTaskItemViewModel)sender!);
            foreach (var run in item.Runs)
            {
                run.OpenConversationRequested += (sender, _) => OpenRunConversationCommand.Execute((CronTaskRunItemViewModel)sender!);
            }
            Tasks.Add(item);
        }

        SelectedTask = previousSelectionId == null
            ? null
            : Tasks.FirstOrDefault(item => string.Equals(item.Id, previousSelectionId, StringComparison.Ordinal));

        OnPropertyChanged(nameof(HasTasks));
    }

    [RelayCommand]
    private async Task SaveTaskAsync()
    {
        if (_taskService == null || !Editor.CanSave) return;

        var draft = Editor.BuildDraft();
        var result = Editor.IsEditing
            ? await _taskService.UpdateAsync(Editor.EditingTaskId!, draft)
            : await _taskService.CreateAsync(draft);

        if (!result.Success)
        {
            Editor.ApplyValidation(result.Validation);
            StatusMessage = result.Validation.FirstMessage;
            return;
        }

        StatusMessage = Editor.IsEditing
            ? L("Cron.Toast.Updated", "Task updated.")
            : L("Cron.Toast.Created", "Task created.");
        Editor.ResetToNew();
    }

    [RelayCommand]
    private void EditTask(CronTaskItemViewModel? item)
    {
        if (item == null) return;
        SelectedTask = item;
        Editor.LoadForEdit(item.Task);
    }

    [RelayCommand]
    private void CancelEdit()
    {
        Editor.ResetToNew();
        StatusMessage = string.Empty;
    }

    [RelayCommand]
    private async Task ToggleTaskEnabledAsync(CronTaskItemViewModel? item)
    {
        if (_taskService == null || item == null) return;
        var result = await _taskService.SetEnabledAsync(item.Id, !item.IsEnabled);
        StatusMessage = result.Success
            ? (result.Task!.IsEnabled ? L("Cron.Toast.Resumed", "Task resumed.") : L("Cron.Toast.Paused", "Task paused."))
            : result.Validation.FirstMessage;
    }

    [RelayCommand]
    private async Task RunTaskNowAsync(CronTaskItemViewModel? item)
    {
        if (_executionWorker == null || item == null) return;

        var claim = await _executionWorker.RunNowAsync(item.Id);
        StatusMessage = claim == null
            ? L("Cron.Toast.RunNowFailed", "Could not queue a manual run.")
            : L("Cron.Toast.RunNowQueued", "A manual run was queued; it opens its own new session and does not change the next scheduled run.");
    }

    [RelayCommand]
    private async Task DeleteTaskAsync(CronTaskItemViewModel? item)
    {
        if (_taskService == null || item == null) return;

        var confirmation = await MessageBox.ShowAsync(
            message: string.Format(
                L("Cron.Confirm.Delete", "Delete the scheduled task \"{0}\"? Sessions it already created are kept."),
                item.Name),
            title: L("Dialog.Title.Warning", "Warning"),
            button: MessageBoxButton.OKCancel,
            icon: MessageBoxIcon.Warning);

        if (confirmation != MessageBoxResult.OK) return;

        await _taskService.DeleteAsync(item.Id);
        if (string.Equals(Editor.EditingTaskId, item.Id, StringComparison.Ordinal)) Editor.ResetToNew();
        StatusMessage = L("Cron.Toast.Deleted", "Task deleted.");
    }

    [RelayCommand]
    private void OpenRunConversation(CronTaskRunItemViewModel? run)
    {
        if (run == null || _navigator == null || !run.CanOpenConversation) return;

        StatusMessage = _navigator.TryNavigateToConversation(run.HistoryId, run.ConversationId)
            ? string.Empty
            : L("Cron.Toast.ConversationMissing", "That run's session is no longer available.");
    }

    private static void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    private string L(string key, string fallback)
        => _localizationService?.GetString(key, fallback) ?? fallback;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_taskService != null) _taskService.TasksChanged -= OnTasksChanged;
        if (_localizationService != null) _localizationService.LanguageChanged -= OnLanguageChanged;
        Editor.Dispose();
    }
}
