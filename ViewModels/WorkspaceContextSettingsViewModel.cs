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
    [ObservableProperty] private bool _overrideToolResultClearing;
    [ObservableProperty] private bool _toolResultClearingEnabled;
    [ObservableProperty] private bool _overrideKeepRecentToolResultChars;
    [ObservableProperty] private long _keepRecentToolResultChars;
    [ObservableProperty] private bool _overrideSummaryMaxTokens;
    [ObservableProperty] private long _summaryMaxTokens;
    [ObservableProperty] private bool _overrideWorkspaceKnowledgeBudget;
    [ObservableProperty] private int _workspaceKnowledgeCharBudget;
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
    public string ToolResultClearingSourceText => Source(OverrideToolResultClearing);
    public string KeepRecentToolResultCharsSourceText => Source(OverrideKeepRecentToolResultChars);
    public string SummaryMaxTokensSourceText => Source(OverrideSummaryMaxTokens);
    public string WorkspaceKnowledgeSourceText => Source(OverrideWorkspaceKnowledgeBudget);
    public string EffectiveContextCapText => _effective == null ? "—" : _effective.ContextWindowTokens.ToString("N0");
    public string EffectiveAutoCompressText => _effective == null ? "—" : (_effective.AutoCompress ? L("Common.Enabled", "Enabled") : L("Common.Disabled", "Disabled"));
    public string EffectiveCompressionThresholdText => _effective == null ? "—" : _effective.CompressionThresholdTokens.ToString("N0");
    public string EffectiveToolResultClearingText => _effective == null ? "—" : (_effective.ToolResultClearingEnabled ? L("Common.Enabled", "Enabled") : L("Common.Disabled", "Disabled"));
    public string EffectiveKeepRecentToolResultCharsText => _effective == null ? "—" : _effective.KeepRecentToolResultChars.ToString("N0");
    public string EffectiveSummaryMaxTokensText => _effective == null ? "—" : _effective.SummaryMaxTokens.ToString("N0");
    public string EffectiveWorkspaceKnowledgeText => (OverrideWorkspaceKnowledgeBudget ? WorkspaceKnowledgeCharBudget : _appConfig.WorkspaceKnowledgeCharBudget).ToString("N0");

    partial void OnOverrideContextCapChanged(bool value) { if (!_loadingDraft) Refresh(); }
    partial void OnContextCapTokensChanged(long value) { if (!_loadingDraft) Refresh(); }
    partial void OnOverrideAutoCompressChanged(bool value) { if (!_loadingDraft) Refresh(); }
    partial void OnAutoCompressChanged(bool value) { if (!_loadingDraft) Refresh(); }
    partial void OnOverrideCompressionThresholdChanged(bool value) { if (!_loadingDraft) Refresh(); }
    partial void OnCompressionThresholdTokensChanged(long value) { if (!_loadingDraft) Refresh(); }
    partial void OnOverrideToolResultClearingChanged(bool value) { if (!_loadingDraft) Refresh(); }
    partial void OnToolResultClearingEnabledChanged(bool value) { if (!_loadingDraft) Refresh(); }
    partial void OnOverrideKeepRecentToolResultCharsChanged(bool value) { if (!_loadingDraft) Refresh(); }
    partial void OnKeepRecentToolResultCharsChanged(long value) { if (!_loadingDraft) Refresh(); }
    partial void OnOverrideSummaryMaxTokensChanged(bool value) { if (!_loadingDraft) Refresh(); }
    partial void OnSummaryMaxTokensChanged(long value) { if (!_loadingDraft) Refresh(); }
    partial void OnOverrideWorkspaceKnowledgeBudgetChanged(bool value) { if (!_loadingDraft) Refresh(); }
    partial void OnWorkspaceKnowledgeCharBudgetChanged(int value) { if (!_loadingDraft) Refresh(); }

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
        OverrideToolResultClearing = source?.ToolResultClearingEnabled.HasValue == true;
        ToolResultClearingEnabled = source?.ToolResultClearingEnabled ?? effective?.ToolResultClearingEnabled ?? _appConfig.ContextPolicy.ToolResultClearingEnabled;
        OverrideKeepRecentToolResultChars = source?.KeepRecentToolResultChars.HasValue == true;
        KeepRecentToolResultChars = source?.KeepRecentToolResultChars ?? effective?.KeepRecentToolResultChars ?? _appConfig.ContextPolicy.KeepRecentToolResultChars;
        OverrideSummaryMaxTokens = source?.SummaryMaxTokens.HasValue == true;
        SummaryMaxTokens = source?.SummaryMaxTokens ?? effective?.SummaryMaxTokens ?? _appConfig.ContextPolicy.SummaryMaxTokens;
        OverrideWorkspaceKnowledgeBudget = source?.WorkspaceKnowledgeCharBudget.HasValue == true;
        WorkspaceKnowledgeCharBudget = source?.WorkspaceKnowledgeCharBudget ?? _appConfig.WorkspaceKnowledgeCharBudget;
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
            : _effective.BudgetSummary;
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
        OnPropertyChanged(nameof(ToolResultClearingSourceText));
        OnPropertyChanged(nameof(KeepRecentToolResultCharsSourceText));
        OnPropertyChanged(nameof(SummaryMaxTokensSourceText));
        OnPropertyChanged(nameof(WorkspaceKnowledgeSourceText));
        OnPropertyChanged(nameof(EffectiveContextCapText));
        OnPropertyChanged(nameof(EffectiveAutoCompressText));
        OnPropertyChanged(nameof(EffectiveCompressionThresholdText));
        OnPropertyChanged(nameof(EffectiveToolResultClearingText));
        OnPropertyChanged(nameof(EffectiveKeepRecentToolResultCharsText));
        OnPropertyChanged(nameof(EffectiveSummaryMaxTokensText));
        OnPropertyChanged(nameof(EffectiveWorkspaceKnowledgeText));
    }

    private string Validate()
    {
        if (OverrideContextCap && ContextCapTokens < 1_024)
            return L("WorkspaceContext.Error.ContextCap", "Context cap must be at least 1,024.");
        if (OverrideCompressionThreshold && CompressionThresholdTokens <= 0)
            return L("WorkspaceContext.Error.Threshold", "Compression threshold must be positive.");
        if (OverrideKeepRecentToolResultChars
            && (KeepRecentToolResultChars < AppContextPolicy.MinKeepRecentToolResultChars
                || KeepRecentToolResultChars > AppContextPolicy.MaxKeepRecentToolResultChars))
            return L("WorkspaceContext.Error.KeepToolChars", "Kept tool-result characters must be between 20,000 and 800,000.");
        if (OverrideSummaryMaxTokens
            && (SummaryMaxTokens < AppContextPolicy.MinSummaryMaxTokens
                || SummaryMaxTokens > AppContextPolicy.MaxSummaryMaxTokens))
            return L("WorkspaceContext.Error.SummaryMax", "Summary length cap must be between 1,024 and 32,768 tokens.");
        if (OverrideWorkspaceKnowledgeBudget && WorkspaceKnowledgeCharBudget is < 0 or > AppConfig.MaxWorkspaceKnowledgeCharBudget)
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
            ToolResultClearingEnabled = OverrideToolResultClearing ? ToolResultClearingEnabled : null,
            KeepRecentToolResultChars = OverrideKeepRecentToolResultChars ? KeepRecentToolResultChars : null,
            SummaryMaxTokens = OverrideSummaryMaxTokens ? SummaryMaxTokens : null,
            WorkspaceKnowledgeCharBudget = OverrideWorkspaceKnowledgeBudget ? WorkspaceKnowledgeCharBudget : null
        };
        return HasAny(draft) ? draft : null;
    }

    private static bool HasAny(WorkspaceContextPolicyOverride value) =>
        value.ContextCapTokens.HasValue
        || value.AutoCompress.HasValue
        || value.CompressionThresholdTokens.HasValue
        || value.ToolResultClearingEnabled.HasValue
        || value.KeepRecentToolResultChars.HasValue
        || value.SummaryMaxTokens.HasValue
        || value.WorkspaceKnowledgeCharBudget.HasValue;

    private static WorkspaceContextPolicyOverride? Clone(WorkspaceContextPolicyOverride? source) => source == null
        ? null
        : new WorkspaceContextPolicyOverride
        {
            ContextCapTokens = source.ContextCapTokens,
            AutoCompress = source.AutoCompress,
            CompressionThresholdTokens = source.CompressionThresholdTokens,
            ToolResultClearingEnabled = source.ToolResultClearingEnabled,
            KeepRecentToolResultChars = source.KeepRecentToolResultChars,
            SummaryMaxTokens = source.SummaryMaxTokens,
            WorkspaceKnowledgeCharBudget = source.WorkspaceKnowledgeCharBudget
        };

    private static bool Equivalent(WorkspaceContextPolicyOverride? left, WorkspaceContextPolicyOverride? right) =>
        left?.ContextCapTokens == right?.ContextCapTokens
        && left?.AutoCompress == right?.AutoCompress
        && left?.CompressionThresholdTokens == right?.CompressionThresholdTokens
        && left?.ToolResultClearingEnabled == right?.ToolResultClearingEnabled
        && left?.KeepRecentToolResultChars == right?.KeepRecentToolResultChars
        && left?.SummaryMaxTokens == right?.SummaryMaxTokens
        && left?.WorkspaceKnowledgeCharBudget == right?.WorkspaceKnowledgeCharBudget;

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
