using Athena.UI.Services.ComputerUse;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public sealed partial class ComputerUseViewModel : ViewModelBase
{
    private readonly ComputerUseService _service;
    private readonly ILocalizationService _localization;

    [ObservableProperty] private IReadOnlyList<ComputerUseTarget> _windows = [];
    [ObservableProperty] private ComputerUseTarget? _selectedWindow;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _hasFrame;
    [ObservableProperty] private string _detail = "";

    public ComputerUseViewModel(ComputerUseService service, ILocalizationService localization)
    {
        _service = service;
        _localization = localization;
        Status = L("ComputerUse.Ready", "Ready");
    }

    public async Task RefreshAsync(CancellationToken token)
    {
        try
        {
            var previous = SelectedWindow;
            Windows = await _service.ListWindowsAsync(token);
            SelectedWindow = Windows.FirstOrDefault(candidate => candidate == previous);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = L("ComputerUse.Unavailable", "Computer Use unavailable");
            Detail = exception.Message;
        }
    }

    partial void OnSelectedWindowChanged(ComputerUseTarget? value)
    {
        _service.SelectedTarget = value;
        if (value is not null)
        {
            Status = L("ComputerUse.TargetReady", "Window selected. Ask Athena to operate it in chat.");
            Detail = value.Title;
        }
    }

    public void Update(ComputerUseProgress progress)
    {
        HasFrame |= progress.Frame is not null;
        Status = progress.Phase switch
        {
            ComputerUsePhase.Observing => L("ComputerUse.Observing", "Observing window"),
            ComputerUsePhase.Moving => L("ComputerUse.Moving", "Moving the AI cursor"),
            ComputerUsePhase.Clicking => L("ComputerUse.Clicking", "Clicked"),
            ComputerUsePhase.Typing => L("ComputerUse.Typing", "Entering text"),
            ComputerUsePhase.OtherAction => L("ComputerUse.OtherAction", "Input sent"),
            ComputerUsePhase.Completed => L("ComputerUse.Completed", "Completed"),
            ComputerUsePhase.Stopped => L("ComputerUse.Stopped", "Stopped"),
            _ => L("ComputerUse.Failed", "Computer Use could not finish")
        };
        if (progress.Detail is not null)
            Detail = progress.Detail;
    }

    public string L(string key, string fallback) => _localization.GetString(key, fallback);
}
