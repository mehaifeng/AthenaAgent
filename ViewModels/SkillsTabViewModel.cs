using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Athena.UI.Models;
using Athena.UI.Models.Skills;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athena.UI.ViewModels;

/// <summary>Skill catalogue page. It manages disclosure state, not the contents of external Skills.</summary>
public partial class SkillsTabViewModel : ViewModelBase
{
    private readonly ISkillCatalogService? _catalog;
    private readonly IConfigService? _configService;
    private readonly IWorkspaceService? _workspaceService;
    private readonly ILocalizationService? _localization;
    private readonly IUserInteractionService? _userInteraction;

    [ObservableProperty] private AppConfig _config = new();
    [ObservableProperty] private ObservableCollection<SkillItemViewModel> _skills = new();
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _applicationSkillsDirectory = string.Empty;

    public SkillsTabViewModel() : this(null, null, null, null, null) { }

    public SkillsTabViewModel(
        ISkillCatalogService? catalog,
        IConfigService? configService,
        IWorkspaceService? workspaceService,
        ILocalizationService? localization,
        IUserInteractionService? userInteraction)
    {
        _catalog = catalog;
        _configService = configService;
        _workspaceService = workspaceService;
        _localization = localization;
        _userInteraction = userInteraction;
        ApplicationSkillsDirectory = catalog?.ApplicationSkillsDirectory ?? string.Empty;
    }

    public void Initialize(ConfigTabViewModel configOwner)
    {
        Config = configOwner.Config;
        configOwner.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(ConfigTabViewModel.Config))
            {
                Config = configOwner.Config;
                RefreshSkills();
            }
        };
        if (_workspaceService != null)
        {
            _workspaceService.ActiveWorkspaceChanged += (_, _) => RefreshSkills();
        }
        RefreshSkills();
    }

    [RelayCommand]
    private void RefreshSkills()
    {
        if (_catalog == null) return;
        var workspacePath = _workspaceService?.ActiveWorkspace?.DirectoryPath;
        var snapshot = _catalog.GetSnapshot(workspacePath, forceRefresh: true);
        Skills = new ObservableCollection<SkillItemViewModel>(snapshot.Skills.Select(skill =>
            new SkillItemViewModel(skill, SetSkillEnabledAsync)));
        var active = snapshot.EffectiveSkills.Count;
        var invalid = snapshot.Skills.Count(skill => skill.HasErrors);
        var disabled = snapshot.Skills.Count(skill => !skill.IsEnabled);
        Status = $"{active} active · {disabled} disabled · {invalid} issues";
    }

    [RelayCommand]
    private async Task SetSkillEnabledAsync(SkillItemViewModel? item)
    {
        if (item == null || _configService == null) return;
        var key = item.Skill.StableKey;
        if (item.IsEnabled)
        {
            while (Config.DisabledSkillKeys.Remove(key)) { }
        }
        else if (!Config.DisabledSkillKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            Config.DisabledSkillKeys.Add(key);
        }
        await _configService.SaveAsync(Config);
        RefreshSkills();
    }

    [RelayCommand]
    private void ToggleExpanded(SkillItemViewModel? item)
    {
        if (item != null) item.IsExpanded = !item.IsExpanded;
    }

    [RelayCommand]
    private void OpenSkillsDirectory() => OpenPath(ApplicationSkillsDirectory);

    [RelayCommand]
    private void OpenSkillFile(SkillItemViewModel? item)
    {
        if (item != null) OpenPath(item.Skill.SkillFilePath);
    }

    [RelayCommand]
    private async Task ImportArchiveAsync()
    {
        if (_catalog == null || _userInteraction == null) return;
        var files = await _userInteraction.PickFilesAsync(
            GetString("Skills.ImportArchivePicker", "Select one Skill ZIP archive"),
            "ZIP archives", ["*.zip"], allowMultiple: false);
        if (files.Count == 1) await ImportSkillAsync(files[0], isArchive: true);
    }

    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        if (_catalog == null || _userInteraction == null) return;
        var folder = await _userInteraction.PickFolderAsync(GetString("Skills.ImportFolderPicker", "Select one Skill folder"));
        if (!string.IsNullOrWhiteSpace(folder)) await ImportSkillAsync(folder, isArchive: false);
    }

    [RelayCommand]
    private async Task DeleteSkillAsync(SkillItemViewModel? item)
    {
        if (item == null || _catalog == null || _userInteraction == null) return;
        var confirmed = await _userInteraction.ConfirmAsync(
            GetString("Skills.DeleteTitle", "Remove Skill"),
            string.Format(GetString("Skills.DeleteConfirm", "Remove the Skill '{0}'? This cannot be undone."), item.Skill.Name),
            GetString("Skills.Delete", "Remove"),
            GetString("Common.Cancel", "Cancel"));
        if (!confirmed) return;

        if (await _catalog.DeleteSkillAsync(item.Skill))
        {
            if (_configService != null)
            {
                while (Config.DisabledSkillKeys.Remove(item.Skill.StableKey)) { }
                await _configService.SaveAsync(Config);
            }
            RefreshSkills();
        }
        else
        {
            Status = GetString("Skills.DeleteFailed", "Could not remove this Skill.");
        }
    }

    private async Task ImportSkillAsync(string sourcePath, bool isArchive)
    {
        if (_catalog == null) return;
        var validation = await _catalog.ValidateImportAsync(sourcePath, isArchive);
        if (!validation.IsValid)
        {
            Status = validation.Message;
            return;
        }
        var result = await _catalog.ImportAsync(sourcePath, isArchive);
        Status = result.Message;
        if (result.IsValid) RefreshSkills();
    }

    private static void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", $"\"{path}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private string GetString(string key, string fallback) => _localization?.GetString(key, fallback) ?? fallback;
}

public partial class SkillItemViewModel : ViewModelBase
{
    private readonly Func<SkillItemViewModel?, Task> _onEnabledChanged;
    public SkillDescriptor Skill { get; }
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isEnabled;

    public SkillItemViewModel(SkillDescriptor skill, Func<SkillItemViewModel?, Task> onEnabledChanged)
    {
        Skill = skill;
        _isEnabled = skill.IsEnabled;
        _onEnabledChanged = onEnabledChanged;
    }

    public string SourceLabel => Skill.SourceScope == SkillSourceScope.Project ? "Project" : "Athena";
    public string StatusLabel => Skill.HasErrors ? "Invalid" : !IsEnabled ? "Disabled" : !Skill.IsEffective ? "Shadowed" : "Active";
    public string Diagnostics => string.Join(Environment.NewLine, Skill.ValidationIssues.Select(issue => issue.Message));
    public string Resources => Skill.ResourceDirectories.Count == 0 ? "No bundled resources" : string.Join(", ", Skill.ResourceDirectories);
    public bool CanDelete => true;

    partial void OnIsEnabledChanged(bool value) => _ = _onEnabledChanged(this);
}
