using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public partial class SpeechSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly AppConfigurationSession _configurationSession;
    private readonly IChatService? _chatService;
    private readonly ISystemAudioService? _systemAudioService;
    private readonly ILocalizationService? _localizationService;
    private CancellationTokenSource? _outputTestCancellation;
    private CancellationTokenSource? _audioTestCancellation;
    private bool _disposed;

    public SpeechSettingsViewModel(
        AppConfigurationSession configurationSession,
        IChatService? chatService = null,
        ISystemAudioService? systemAudioService = null,
        ILocalizationService? localizationService = null)
    {
        _configurationSession = configurationSession;
        _chatService = chatService;
        _systemAudioService = systemAudioService;
        _localizationService = localizationService;
        _config = configurationSession.Current;
        _configurationSession.CurrentChanged += OnCurrentConfigChanged;
        RebuildCards();
    }

    [ObservableProperty] private AppConfig _config;
    [ObservableProperty] private IReadOnlyList<ExtensionProviderCardViewModel> _providerCards = [];
    [ObservableProperty] private string _testStatus = string.Empty;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(TestOutputCommand))] private bool _isTesting;
    [ObservableProperty] private ChatAttachment? _testAttachment;

    public bool CanTest => !IsTesting;
    public string PlaybackGlyph => TestAttachment?.IsPlaying == true ? "■" : "▶";

    private void RebuildCards()
    {
        var providers = ExtensionProviderCatalog.AudioProviders;
        if (providers.All(provider => provider.Id != Config.ChatAudioProvider))
            Config.ChatAudioProvider = "Edge";

        ExtensionSettingsSupport.EnsureSettings(
            Config.AudioProviderSettings,
            providers,
            Config.ChatAudioProvider,
            selected =>
            {
                if (Config.ChatAudioProvider == "OpenAI")
                {
                    selected.BaseUrl = Config.ChatAudioBaseUrl;
                    selected.ApiKey = Config.ChatAudioApiKey;
                    selected.Model = Config.ChatAudioModel;
                    selected.Voice = Config.ChatAudioVoice;
                }
                selected.Language = string.IsNullOrWhiteSpace(Config.ChatAudioLanguage) ? "en-US" : Config.ChatAudioLanguage;
                selected.Speed = Config.ChatAudioSpeed <= 0 ? 1 : Config.ChatAudioSpeed;
                selected.LocalExecutable = Config.ChatAudioLocalExecutable;
                selected.LocalModelPath = Config.ChatAudioLocalModelPath;
            });

        ProviderCards = providers.Select(option => new ExtensionProviderCardViewModel(
            ExtensionProviderKind.Audio,
            option,
            Config.AudioProviderSettings.First(setting => setting.ProviderId == option.Id),
            option.Id == Config.ChatAudioProvider,
            SelectProvider,
            _localizationService)).ToList();
    }

    private void SelectProvider(ExtensionProviderCardViewModel card)
    {
        Config.ChatAudioProvider = card.Id;
        foreach (var candidate in ProviderCards)
            if (!ReferenceEquals(candidate, card)) candidate.IsSelected = false;
    }

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestOutputAsync()
    {
        if (_chatService == null)
        {
            TestStatus = GetString("Status.ServiceNotInitialized", "Service not initialized");
            return;
        }
        if (!Config.ChatAudioEnabled)
        {
            TestStatus = GetString("Status.EnableAudioOutputFirst", "Please enable chat audio output first");
            return;
        }

        IsTesting = true;
        TestStatus = GetString("Status.TestingConnection", "Testing...");
        TestAttachment = null;
        var cancellation = new CancellationTokenSource();
        _outputTestCancellation = cancellation;
        try
        {
            await _configurationSession.SaveNowAsync();
            var result = await _chatService.TestAudioOutputAsync(cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested) return;
            TestStatus = result.Message;
            TestAttachment = result.Attachment;
            OnPropertyChanged(nameof(PlaybackGlyph));
        }
        catch (OperationCanceledException)
        {
            TestStatus = GetString("Common.Cancelled", "Cancelled");
        }
        finally
        {
            if (ReferenceEquals(_outputTestCancellation, cancellation))
            {
                _outputTestCancellation = null;
                if (!_disposed) IsTesting = false;
            }
            cancellation.Dispose();
        }
    }

    [RelayCommand]
    private void TogglePlayback()
    {
        if (TestAttachment == null) return;
        if (TestAttachment.IsPlaying)
        {
            StopPlayback();
            return;
        }

        StopPlayback();
        if (_systemAudioService?.IsSupported != true)
        {
            TestStatus = GetString("Chat.Audio.PlaybackUnavailable", "Audio playback is unavailable on this device.");
            return;
        }

        _ = RunPlaybackAsync(TestAttachment);
    }

    private async Task RunPlaybackAsync(ChatAttachment attachment)
    {
        var cancellation = new CancellationTokenSource();
        _audioTestCancellation = cancellation;
        attachment.IsPlaying = true;
        OnPropertyChanged(nameof(PlaybackGlyph));
        try
        {
            var result = await _systemAudioService!.PlayFileAsync(attachment.StoredPath, cancellation.Token);
            if (!result.Success && !cancellation.IsCancellationRequested)
                TestStatus = string.Format(GetString("Chat.Audio.PlaybackFailedDetail", "Failed to play system audio: {0}"), result.Message);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_audioTestCancellation == cancellation)
            {
                _audioTestCancellation.Dispose();
                _audioTestCancellation = null;
                attachment.IsPlaying = false;
                OnPropertyChanged(nameof(PlaybackGlyph));
            }
        }
    }

    private void StopPlayback()
    {
        _audioTestCancellation?.Cancel();
        _audioTestCancellation?.Dispose();
        _audioTestCancellation = null;
        if (TestAttachment != null) TestAttachment.IsPlaying = false;
        OnPropertyChanged(nameof(PlaybackGlyph));
    }

    private void OnCurrentConfigChanged(object? sender, AppConfig config)
    {
        Config = config;
        RebuildCards();
    }

    private string GetString(string key, string fallback) => _localizationService?.GetString(key, fallback) ?? fallback;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _outputTestCancellation?.Cancel();
        _outputTestCancellation = null;
        StopPlayback();
        _configurationSession.CurrentChanged -= OnCurrentConfigChanged;
    }
}
