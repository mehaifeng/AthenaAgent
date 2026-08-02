using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

/// <summary>
/// Explicit Workspace policy editor. Every editable value lives in this draft until Save
/// durably commits it through IWorkspaceService; Cancel never mutates the live profile.
/// </summary>
public sealed partial class WorkspaceContextSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly WorkspaceProfile _workspace;
    private readonly AppConfig _appConfig;
    private readonly IContextPolicyProvider _policyProvider;
    private readonly IWorkspaceService _workspaceService;
    private readonly ILocalizationService? _localization;
    private WorkspaceContextPolicyOverride? _baseline;
    private ResolvedContextPolicy? _effective;
    private bool _loadingDraft;

    public WorkspaceContextSettingsViewModel(
        WorkspaceProfile workspace,
        AppConfig appConfig,
        IContextPolicyProvider policyProvider,
        IWorkspaceService workspaceService,
        ILocalizationService? localization = null)
    {
        _workspace = workspace;
        _appConfig = appConfig;
        _policyProvider = policyProvider;
        _workspaceService = workspaceService;
        _localization = localization;
        _baseline = Clone(workspace.ContextPolicyOverride);
        LoadDraft(_baseline);
        if (_localization != null) _localization.LanguageChanged += OnLanguageChanged;
    }

    public string WorkspaceName => _workspace.Name;
    public event EventHandler? CloseRequested;

    [ObservableProperty] private bool _overrideContextCap;
    [ObservableProperty] private long _contextCapTokens;
    [ObservableProperty] private bool _overrideAutoCompress;
    [ObservableProperty] private bool _autoCompress;
    [ObservableProperty] private bool _overrideCompressionThreshold;
    [ObservableProperty] private long _compressionThresholdTokens;
    [ObservableProperty] private bool _overrideKeepRecentRounds;
    [ObservableProperty] private int _keepRecentRounds;
    [ObservableProperty] private bool _overrideTargetSummaryTokens;
    [ObservableProperty] private long _targetSummaryTokens;
    [ObservableProperty] private bool _overrideWorkspaceKnowledgeBudget;
    [ObservableProperty] private int _workspaceKnowledgeTokenBudget;
    [ObservableProperty] private string _effectivePolicyText = string.Empty;
    [ObservableProperty] private string _sourceText = string.Empty;
    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private bool _isSaving;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);
    public bool CanSave => !IsSaving && !HasError;
    public bool IsDirty => !Equivalent(_baseline, BuildDraft());
    public string ContextCapSourceText => Source(OverrideContextCap, _effective?.ContextWindowSource);
    public string AutoCompressSourceText => Source(OverrideAutoCompress);
    public string CompressionThresholdSourceText => Source(OverrideCompressionThreshold, _effective?.CompressionThresholdSource);
    public string KeepRecentRoundsSourceText => Source(OverrideKeepRecentRounds);
    public string TargetSummaryTokensSourceText => Source(OverrideTargetSummaryTokens);
    public string WorkspaceKnowledgeSourceText => Source(OverrideWorkspaceKnowledgeBudget);
    public string EffectiveContextCapText => _effective == null ? "—" : _effective.ContextWindowTokens.ToString("N0");
    public string EffectiveAutoCompressText => _effective == null ? "—" : (_effective.AutoCompress ? L("Common.Enabled", "Enabled") : L("Common.Disabled", "Disabled"));
    public string EffectiveCompressionThresholdText => _effective == null ? "—" : _effective.CompressionThresholdTokens.ToString("N0");
    public string EffectiveKeepRecentRoundsText => _effective == null ? "—" : _effective.KeepRecentRounds.ToString();
    public string EffectiveTargetSummaryTokensText => _effective == null ? "—" : _effective.TargetSummaryTokens.ToString("N0");
    public string EffectiveWorkspaceKnowledgeText => (OverrideWorkspaceKnowledgeBudget ? WorkspaceKnowledgeTokenBudget : _appConfig.WorkspaceKnowledgeTokenBudget).ToString("N0");

    partial void OnOverrideContextCapChanged(bool value) { if (!_loadingDraft) Refresh(); }
    partial void OnContextCapTokensChanged(long value) { if (!_loadingDraft) Refresh(); }
    partial void OnOverrideAutoCompressChanged(bool value) { if (!_loadingDraft) Refresh(); }
    partial void OnAutoCompressChanged(bool value) { if (!_loadingDraft) Refresh(); }
    partial void OnOverrideCompressionThresholdChanged(bool value) { if (!_loadingDraft) Refresh(); }
    partial void OnCompressionThresholdTokensChanged(long value) { if (!_loadingDraft) Refresh(); }
    partial void OnOverrideKeepRecentRoundsChanged(bool value) { if (!_loadingDraft) Refresh(); }
    partial void OnKeepRecentRoundsChanged(int value) { if (!_loadingDraft) Refresh(); }
    partial void OnOverrideTargetSummaryTokensChanged(bool value) { if (!_loadingDraft) Refresh(); }
    partial void OnTargetSummaryTokensChanged(long value) { if (!_loadingDraft) Refresh(); }
    partial void OnOverrideWorkspaceKnowledgeBudgetChanged(bool value) { if (!_loadingDraft) Refresh(); }
    partial void OnWorkspaceKnowledgeTokenBudgetChanged(int value) { if (!_loadingDraft) Refresh(); }

    partial void OnErrorTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(CanSave));
        SaveCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsSavingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSave));
        SaveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Refresh();
        if (HasError) return;
        IsSaving = true;
        try
        {
            var draft = BuildDraft();
            await _workspaceService.UpdateContextPolicyAsync(_workspace, draft);
            _baseline = Clone(draft);
            OnPropertyChanged(nameof(IsDirty));
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ErrorText = string.Format(
                L("WorkspaceContext.Error.Save", "Could not save Workspace context settings: {0}"),
                ex.Message);
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void LoadDraft(WorkspaceContextPolicyOverride? source)
    {
        var effective = _policyProvider.Resolve(source)?.Policy;
        _loadingDraft = true;
        OverrideContextCap = source?.ContextCapTokens.HasValue == true;
        ContextCapTokens = source?.ContextCapTokens ?? effective?.ContextWindowTokens ?? 1_000_000;
        OverrideAutoCompress = source?.AutoCompress.HasValue == true;
        AutoCompress = source?.AutoCompress ?? effective?.AutoCompress ?? _appConfig.ContextPolicy.AutoCompress;
        OverrideCompressionThreshold = source?.CompressionThresholdTokens.HasValue == true;
        CompressionThresholdTokens = source?.CompressionThresholdTokens ?? effective?.CompressionThresholdTokens ?? 262_144;
        OverrideKeepRecentRounds = source?.KeepRecentRounds.HasValue == true;
        KeepRecentRounds = source?.KeepRecentRounds ?? effective?.KeepRecentRounds ?? _appConfig.ContextPolicy.KeepRecentRounds;
        OverrideTargetSummaryTokens = source?.TargetSummaryTokens.HasValue == true;
        TargetSummaryTokens = source?.TargetSummaryTokens ?? effective?.TargetSummaryTokens ?? _appConfig.ContextPolicy.TargetSummaryTokens;
        OverrideWorkspaceKnowledgeBudget = source?.WorkspaceKnowledgeTokenBudget.HasValue == true;
        WorkspaceKnowledgeTokenBudget = source?.WorkspaceKnowledgeTokenBudget ?? _appConfig.WorkspaceKnowledgeTokenBudget;
        _loadingDraft = false;
        Refresh();
    }

    private void Refresh()
    {
        ErrorText = Validate();
        var draft = BuildDraft();
        _effective = _policyProvider.Resolve(draft)?.Policy;
        EffectivePolicyText = _effective == null
            ? L("Settings.Context.Unconfigured", "Not configured")
            : $"W {_effective.ContextWindowTokens:N0} · R {_effective.OutputReserveTokens:N0} · S {_effective.SafetyMarginTokens:N0} · B {_effective.AvailableInputBudgetTokens:N0} · T {_effective.CompressionThresholdTokens:N0}";
        if (_effective == null)
        {
            SourceText = "—";
        }
        else
        {
            SourceText = string.Format(
                L("WorkspaceContext.Source.Format", "Context {0} · Threshold {1} · remaining fields {2}"),
                L($"WorkspaceContext.Source.{_effective.ContextWindowSource}", _effective.ContextWindowSource.ToString()),
                L($"WorkspaceContext.Source.{_effective.CompressionThresholdSource}", _effective.CompressionThresholdSource.ToString()),
                L("WorkspaceContext.Source.FieldInheritance", "Workspace override / App default by field"));
        }
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(ContextCapSourceText));
        OnPropertyChanged(nameof(AutoCompressSourceText));
        OnPropertyChanged(nameof(CompressionThresholdSourceText));
        OnPropertyChanged(nameof(KeepRecentRoundsSourceText));
        OnPropertyChanged(nameof(TargetSummaryTokensSourceText));
        OnPropertyChanged(nameof(WorkspaceKnowledgeSourceText));
        OnPropertyChanged(nameof(EffectiveContextCapText));
        OnPropertyChanged(nameof(EffectiveAutoCompressText));
        OnPropertyChanged(nameof(EffectiveCompressionThresholdText));
        OnPropertyChanged(nameof(EffectiveKeepRecentRoundsText));
        OnPropertyChanged(nameof(EffectiveTargetSummaryTokensText));
        OnPropertyChanged(nameof(EffectiveWorkspaceKnowledgeText));
    }

    private string Validate()
    {
        if (OverrideContextCap && ContextCapTokens < 1_024)
            return L("WorkspaceContext.Error.ContextCap", "Context cap must be at least 1,024.");
        if (OverrideCompressionThreshold && CompressionThresholdTokens <= 0)
            return L("WorkspaceContext.Error.Threshold", "Compression threshold must be positive.");
        if (OverrideKeepRecentRounds && KeepRecentRounds is < 1 or > 50)
            return L("WorkspaceContext.Error.Keep", "Keep recent rounds must be between 1 and 50.");
        if (OverrideTargetSummaryTokens && TargetSummaryTokens is < 128 or > 65_536)
            return L("WorkspaceContext.Error.Target", "Target summary tokens must be between 128 and 65,536.");
        if (OverrideWorkspaceKnowledgeBudget && WorkspaceKnowledgeTokenBudget is < 0 or > 100_000)
            return L("WorkspaceContext.Error.Knowledge", "Workspace knowledge budget must be between 0 and 100,000.");
        return string.Empty;
    }

    private WorkspaceContextPolicyOverride? BuildDraft()
    {
        var draft = new WorkspaceContextPolicyOverride
        {
            ContextCapTokens = OverrideContextCap ? ContextCapTokens : null,
            AutoCompress = OverrideAutoCompress ? AutoCompress : null,
            CompressionThresholdTokens = OverrideCompressionThreshold ? CompressionThresholdTokens : null,
            KeepRecentRounds = OverrideKeepRecentRounds ? KeepRecentRounds : null,
            TargetSummaryTokens = OverrideTargetSummaryTokens ? TargetSummaryTokens : null,
            WorkspaceKnowledgeTokenBudget = OverrideWorkspaceKnowledgeBudget ? WorkspaceKnowledgeTokenBudget : null
        };
        return HasAny(draft) ? draft : null;
    }

    private static bool HasAny(WorkspaceContextPolicyOverride value) =>
        value.ContextCapTokens.HasValue
        || value.AutoCompress.HasValue
        || value.CompressionThresholdTokens.HasValue
        || value.KeepRecentRounds.HasValue
        || value.TargetSummaryTokens.HasValue
        || value.WorkspaceKnowledgeTokenBudget.HasValue;

    private static WorkspaceContextPolicyOverride? Clone(WorkspaceContextPolicyOverride? source) => source == null
        ? null
        : new WorkspaceContextPolicyOverride
        {
            ContextCapTokens = source.ContextCapTokens,
            AutoCompress = source.AutoCompress,
            CompressionThresholdTokens = source.CompressionThresholdTokens,
            KeepRecentRounds = source.KeepRecentRounds,
            TargetSummaryTokens = source.TargetSummaryTokens,
            WorkspaceKnowledgeTokenBudget = source.WorkspaceKnowledgeTokenBudget
        };

    private static bool Equivalent(WorkspaceContextPolicyOverride? left, WorkspaceContextPolicyOverride? right) =>
        left?.ContextCapTokens == right?.ContextCapTokens
        && left?.AutoCompress == right?.AutoCompress
        && left?.CompressionThresholdTokens == right?.CompressionThresholdTokens
        && left?.KeepRecentRounds == right?.KeepRecentRounds
        && left?.TargetSummaryTokens == right?.TargetSummaryTokens
        && left?.WorkspaceKnowledgeTokenBudget == right?.WorkspaceKnowledgeTokenBudget;

    private void OnLanguageChanged(object? sender, EventArgs e) => Refresh();
    private string L(string key, string fallback) => _localization?.GetString(key, fallback) ?? fallback;

    private string Source(bool overridden, ContextPolicyValueSource? inherited = null)
    {
        var source = overridden
            ? ContextPolicyValueSource.WorkspaceOverride
            : inherited ?? ContextPolicyValueSource.AppDefault;
        return L($"WorkspaceContext.Source.{source}", source.ToString());
    }

    public void Dispose()
    {
        if (_localization != null) _localization.LanguageChanged -= OnLanguageChanged;
    }
}
