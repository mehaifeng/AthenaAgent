using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>
/// Owns the application's live configuration instance and is the sole automatic-save coordinator.
/// </summary>
public sealed class AppConfigurationSession : IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly IConfigService _configService;
    private readonly List<Action> _unsubscribers = [];
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private CancellationTokenSource? _saveDelayCancellation;
    private bool _isSaving;
    private bool _isNormalizing;
    private bool _disposed;

    public AppConfigurationSession(IConfigService configService)
    {
        _configService = configService;
        Current = configService.Load();
        Normalize(Current);
        TrackCurrent();
        _configService.ConfigChanged += OnConfigChanged;
    }

    public AppConfig Current { get; private set; }

    public event EventHandler<AppConfig>? CurrentChanged;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task SaveNowAsync()
    {
        ThrowIfDisposed();
        CancelDelayedSave();
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _isNormalizing = true;
            Normalize(Current);
            _isNormalizing = false;

            _isSaving = true;
            await _configService.SaveAsync(Current).ConfigureAwait(false);
        }
        finally
        {
            _isSaving = false;
            _isNormalizing = false;
            _saveGate.Release();
        }
    }

    private void OnConfigChanged(object? sender, AppConfig config)
    {
        if (_isSaving) return;
        if (ReferenceEquals(config, Current))
        {
            CancelDelayedSave();
            return;
        }

        CancelDelayedSave();
        ReplaceCurrent(config);
    }

    private void ReplaceCurrent(AppConfig config)
    {
        UntrackCurrent();
        Current = config;
        Normalize(Current);
        TrackCurrent();
        var replacement = Current;
        if (Dispatcher.UIThread.CheckAccess())
            CurrentChanged?.Invoke(this, replacement);
        else
            Dispatcher.UIThread.Post(() => CurrentChanged?.Invoke(this, replacement));
    }

    private void TrackCurrent()
    {
        TrackPropertyChanges(Current, (_, args) =>
        {
            if (args.PropertyName is nameof(AppConfig.AiModels)
                or nameof(AppConfig.ContextPolicy)
                or nameof(AppConfig.McpServers)
                or nameof(AppConfig.AudioProviderSettings)
                or nameof(AppConfig.ImageProviderSettings)
                or nameof(AppConfig.WebSearchProviderSettings)
                or nameof(AppConfig.MainLayout)
                or nameof(AppConfig.FileSystemPolicy)
                or nameof(AppConfig.AutoAllowedTools)
                or nameof(AppConfig.TerminalAllowlist)
                or nameof(AppConfig.DisabledSkillKeys))
            {
                RebuildTracking();
            }
            RequestSave();
        });

        TrackAiModels(Current.AiModels);
        TrackObservable(Current.ContextPolicy);
        TrackMcpServers(Current.McpServers);
        TrackProviderSettings(Current.AudioProviderSettings);
        TrackProviderSettings(Current.ImageProviderSettings);
        TrackProviderSettings(Current.WebSearchProviderSettings);
        TrackObservable(Current.MainLayout);
        TrackFileSystemPolicy(Current.FileSystemPolicy);
        TrackCollection(Current.AutoAllowedTools, RequestSave);
        TrackCollection(Current.TerminalAllowlist, RequestSave);
        TrackCollection(Current.DisabledSkillKeys, RequestSave);
    }

    private void TrackAiModels(AiModelConfiguration models)
    {
        TrackPropertyChanges(models, (_, _) =>
        {
            RebuildTracking();
            RequestSave();
        });
        TrackCollection(models.Providers, () =>
        {
            RebuildTracking();
            RequestSave();
        });
        TrackCollection(models.ModelMetadataProfiles, () =>
        {
            RebuildTracking();
            RequestSave();
        });
        foreach (var provider in models.Providers)
        {
            TrackPropertyChanges(provider, (_, args) =>
            {
                if (args.PropertyName == nameof(OpenAiProviderConfiguration.ProviderPreset))
                {
                    provider.DisplayName = provider.ProviderPreset;
                    if (ProviderCatalog.TryGetChatBaseUrl(provider.ProviderPreset, out var url))
                        provider.BaseUrl = url;
                }
                RequestSave();
            });
            TrackCollection(provider.Models, RequestSave);
        }

        foreach (var role in GetRoleSettings(models)) TrackObservable(role);
        foreach (var profile in models.ModelMetadataProfiles)
        {
            TrackPropertyChanges(profile, (_, args) =>
            {
                if (args.PropertyName == nameof(ProviderModelMetadataProfile.Overrides))
                {
                    RebuildTracking();
                }
                RequestSave();
            });
            TrackPropertyChanges(profile.Overrides, (_, args) =>
            {
                if (args.PropertyName is nameof(ModelMetadataOverrides.InputModalities)
                    or nameof(ModelMetadataOverrides.OutputModalities))
                {
                    RebuildTracking();
                }
                RequestSave();
            });
            if (profile.Overrides.InputModalities != null)
                TrackCollection(profile.Overrides.InputModalities, RequestSave);
            if (profile.Overrides.OutputModalities != null)
                TrackCollection(profile.Overrides.OutputModalities, RequestSave);
        }
    }

    private void TrackMcpServers(ObservableCollection<McpServerConfig> servers)
    {
        TrackCollection(servers, () =>
        {
            RebuildTracking();
            RequestSave();
        });
        foreach (var server in servers)
        {
            TrackPropertyChanges(server, (_, args) =>
            {
                if (args.PropertyName is nameof(McpServerConfig.Status)
                    or nameof(McpServerConfig.StatusDetail)
                    or nameof(McpServerConfig.DiscoveredToolCount)
                    or nameof(McpServerConfig.IsExpanded))
                {
                    return;
                }
                if (args.PropertyName is nameof(McpServerConfig.Arguments)
                    or nameof(McpServerConfig.Environment)
                    or nameof(McpServerConfig.Headers))
                {
                    RebuildTracking();
                }
                RequestSave();
            });
            TrackNestedMcpCollection(server.Arguments);
            TrackNestedMcpCollection(server.Environment);
            TrackNestedMcpCollection(server.Headers);
        }
    }

    private void TrackNestedMcpCollection<T>(ObservableCollection<T> collection)
        where T : INotifyPropertyChanged
    {
        TrackCollection(collection, () =>
        {
            RebuildTracking();
            RequestSave();
        });
        foreach (var item in collection) TrackObservable(item);
    }

    private void TrackProviderSettings(ObservableCollection<ExtensionProviderSettings> settings)
    {
        TrackCollection(settings, () =>
        {
            RebuildTracking();
            RequestSave();
        });
        foreach (var setting in settings) TrackObservable(setting);
    }

    private void TrackFileSystemPolicy(FileSystemPolicyConfig policy)
    {
        TrackPropertyChanges(policy, (_, _) =>
        {
            RebuildTracking();
            RequestSave();
        });
        TrackObservable(policy.Global);
        TrackPropertyChanges(policy.Platforms, (_, _) =>
        {
            RebuildTracking();
            RequestSave();
        });
        foreach (var platform in new[] { policy.Platforms.Windows, policy.Platforms.MacOS, policy.Platforms.Linux })
        {
            TrackPropertyChanges(platform, (_, _) =>
            {
                RebuildTracking();
                RequestSave();
            });
            TrackObservable(platform.ReadAccess);
            TrackObservable(platform.WriteAccess);
        }
    }

    private void TrackObservable(INotifyPropertyChanged value) =>
        TrackPropertyChanges(value, (_, _) => RequestSave());

    private void TrackPropertyChanges(INotifyPropertyChanged value, PropertyChangedEventHandler handler)
    {
        value.PropertyChanged += handler;
        _unsubscribers.Add(() => value.PropertyChanged -= handler);
    }

    private void TrackCollection<T>(ObservableCollection<T> collection, Action changed)
    {
        NotifyCollectionChangedEventHandler handler = (_, _) => changed();
        collection.CollectionChanged += handler;
        _unsubscribers.Add(() => collection.CollectionChanged -= handler);
    }

    private void RebuildTracking()
    {
        UntrackCurrent();
        TrackCurrent();
    }

    private void UntrackCurrent()
    {
        foreach (var unsubscribe in _unsubscribers.ToArray()) unsubscribe();
        _unsubscribers.Clear();
    }

    private void RequestSave()
    {
        if (_disposed || _isNormalizing) return;

        CancelDelayedSave();
        _saveDelayCancellation = new CancellationTokenSource();
        _ = SaveAfterDelayAsync(_saveDelayCancellation.Token);
    }

    private async Task SaveAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SaveDelay, cancellationToken).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested) await SaveNowAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CancelDelayedSave()
    {
        _saveDelayCancellation?.Cancel();
        _saveDelayCancellation?.Dispose();
        _saveDelayCancellation = null;
    }

    private static void Normalize(AppConfig config)
    {
        AppConfigNormalizer.NormalizeContextPolicy(config);
        AppConfigNormalizer.NormalizeBrowser(config);
    }

    private static IEnumerable<ModelRoleSettings> GetRoleSettings(AiModelConfiguration models)
    {
        yield return models.MainConversation;
        yield return models.TitleGeneration;
        yield return models.ContextCompression;
        yield return models.Approval;
        yield return models.Embedding;
        yield return models.BrowserAgent;
        yield return models.SubAgent;
        yield return models.KnowledgeMaintenance;
        yield return models.ImageRecognition;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelDelayedSave();
        UntrackCurrent();
        _configService.ConfigChanged -= OnConfigChanged;
        _saveGate.Dispose();
    }
}
