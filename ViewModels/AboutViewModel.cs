using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Athena.UI.Services.Interfaces;
using Avalonia.Controls.ApplicationLifetimes;
using Ursa.Controls;
using Serilog;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Athena.UI.Models;

namespace Athena.UI.ViewModels;

public partial class AboutViewModel : ViewModelBase
{
    private const string RepositoryUrl = "https://github.com/mehaifeng/AthenaAgent";
    private const string EnglishGuideUrl = "https://github.com/mehaifeng/AthenaAgent/blob/main/Docs/Athena_User_Guide_EN.md";
    private const string ChineseGuideUrl = "https://github.com/mehaifeng/AthenaAgent/blob/main/Docs/Athena_User_Guide_CN.md";
    private readonly ILocalizationService? _localizationService;
    private readonly IUpdateService? _updateService;
    private readonly ILogger _logger = Log.ForContext<AboutViewModel>();

    [ObservableProperty]
    private bool _isCheckingUpdates;

    [ObservableProperty]
    private string _currentVersion;

    [ObservableProperty]
    private bool _isUpdateProgressVisible;

    [ObservableProperty]
    private bool _isUpdateProgressIndeterminate;

    [ObservableProperty]
    private double _updateProgressValue;

    [ObservableProperty]
    private string _updateProgressStatus = string.Empty;

    /// <summary>本次启动耗时文案（如 "486 ms"）；设计时或未记录时为 "—"。</summary>
    [ObservableProperty]
    private string _startupDurationText;

    public bool CanCheckForUpdates => !IsCheckingUpdates;

    public AboutViewModel() : this(null, null) { }

    public AboutViewModel(ILocalizationService? localizationService, IUpdateService? updateService = null)
    {
        _localizationService = localizationService;
        _updateService = updateService;
        _currentVersion = _updateService?.GetCurrentVersion() ?? "0.0.0";
        _startupDurationText = App.StartupDuration is { } duration
            ? $"{duration.TotalMilliseconds:0} ms"
            : "—";
    }

    partial void OnIsCheckingUpdatesChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCheckForUpdates));
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync()
    {
        if (_updateService == null)
        {
            await MessageBox.ShowAsync(
                message: GetString("About.Update.Error.NoService", "Update service is not available."),
                title: GetString("About.Update.Error.Title", "Update Error"),
                button: MessageBoxButton.OK,
                icon: MessageBoxIcon.Error);
            return;
        }

        IsCheckingUpdates = true;
        try
        {
            var checkResult = await _updateService.CheckForUpdatesAsync();
            CurrentVersion = checkResult.CurrentVersion;
            if (!checkResult.IsSuccess)
            {
                await MessageBox.ShowAsync(
                    message: string.Format(
                        CultureInfo.CurrentCulture,
                        GetString("About.Update.Error.WithReason", "Failed to check updates: {0}"),
                        checkResult.ErrorMessage),
                    title: GetString("About.Update.Error.Title", "Update Error"),
                    button: MessageBoxButton.OK,
                    icon: MessageBoxIcon.Error);
                return;
            }

            if (!checkResult.IsUpdateAvailable)
            {
                await MessageBox.ShowAsync(
                    message: string.Format(
                        CultureInfo.CurrentCulture,
                        GetString("About.Update.UpToDate.Message", "You are on the latest version ({0})."),
                        checkResult.CurrentVersion),
                    title: GetString("About.Update.UpToDate.Title", "No Update Available"),
                    button: MessageBoxButton.OK,
                    icon: MessageBoxIcon.Information);
                return;
            }

            var publishedAt = checkResult.PublishedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
                ?? GetString("About.Update.UnknownDate", "Unknown");
            var releaseNotes = BuildReleaseNotesPreview(checkResult.ReleaseNotes);
            var confirmMessage = string.Format(
                CultureInfo.CurrentCulture,
                GetString(
                    "About.Update.Available.Message",
                    "Current: {0}\nLatest: {1}\nPublished: {2}\n\n{3}\n\nDownload and apply this update now?"),
                checkResult.CurrentVersion,
                checkResult.LatestVersion,
                publishedAt,
                releaseNotes);

            var confirmResult = await MessageBox.ShowAsync(
                message: confirmMessage,
                title: GetString("About.Update.Available.Title", "Update Available"),
                button: MessageBoxButton.OKCancel,
                icon: MessageBoxIcon.Information);

            if (confirmResult != MessageBoxResult.OK)
            {
                return;
            }

            ResetUpdateProgress();
            var progress = new Progress<UpdateProgressInfo>(UpdateProgress);
            var applyResult = await _updateService.PrepareAndLaunchUpdateAsync(checkResult, progress);
            if (!applyResult.IsSuccess)
            {
                if (applyResult.IsPermissionDenied)
                {
                    await MessageBox.ShowAsync(
                        message: GetString(
                            "About.Update.PermissionDenied.Message",
                            "Cannot write to the install directory. Please restart Athena with administrator/root privileges, then try again."),
                        title: GetString("About.Update.PermissionDenied.Title", "Permission Required"),
                        button: MessageBoxButton.OK,
                        icon: MessageBoxIcon.Warning);
                }
                else
                {
                    await MessageBox.ShowAsync(
                        message: string.Format(
                            CultureInfo.CurrentCulture,
                            GetString("About.Update.Error.WithReason", "Failed to check updates: {0}"),
                            applyResult.ErrorMessage),
                        title: GetString("About.Update.Error.Title", "Update Error"),
                        button: MessageBoxButton.OK,
                        icon: MessageBoxIcon.Error);
                }

                return;
            }

            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unexpected error during update flow.");
            await MessageBox.ShowAsync(
                message: string.Format(
                    CultureInfo.CurrentCulture,
                    GetString("About.Update.Error.WithReason", "Failed to check updates: {0}"),
                    ex.Message),
                title: GetString("About.Update.Error.Title", "Update Error"),
                button: MessageBoxButton.OK,
                icon: MessageBoxIcon.Error);
        }
        finally
        {
            ResetUpdateProgress();
            IsCheckingUpdates = false;
        }
    }

    [RelayCommand]
    private void OpenDocumentation()
    {
        var language = _localizationService?.CurrentLanguage ?? "en-US";
        var guideUrl = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? ChineseGuideUrl
            : EnglishGuideUrl;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = guideUrl,
                UseShellExecute = true
            });
            _logger.Information("Opened guide url: {GuideUrl} for language {Language}", guideUrl, language);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to open guide url: {GuideUrl}", guideUrl);
        }
    }

    [RelayCommand]
    private void OpenGitHub()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = RepositoryUrl,
                UseShellExecute = true
            });
            _logger.Information("Opened GitHub repository: {RepositoryUrl}", RepositoryUrl);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to open GitHub repository: {RepositoryUrl}", RepositoryUrl);
        }
    }

    private string GetString(string key, string fallback)
    {
        return _localizationService?.GetString(key, fallback) ?? fallback;
    }

    private void UpdateProgress(UpdateProgressInfo progress)
    {
        IsUpdateProgressVisible = true;
        IsUpdateProgressIndeterminate = !progress.Progress.HasValue;
        UpdateProgressValue = progress.Progress.HasValue
            ? Math.Clamp(progress.Progress.Value * 100d, 0d, 100d)
            : 0d;

        UpdateProgressStatus = progress.Stage switch
        {
            UpdateProgressStage.Preparing => GetString("About.Update.Progress.Preparing", "Preparing update..."),
            UpdateProgressStage.DownloadingManifest => GetString("About.Update.Progress.Manifest", "Downloading update metadata..."),
            UpdateProgressStage.DownloadingPackage => FormatDownloadStatus(progress),
            UpdateProgressStage.VerifyingPackage => GetString("About.Update.Progress.Verifying", "Verifying package..."),
            UpdateProgressStage.ExtractingPackage => GetString("About.Update.Progress.Extracting", "Extracting update package..."),
            UpdateProgressStage.LaunchingUpdater => GetString("About.Update.Progress.Launching", "Launching updater..."),
            _ => string.Empty
        };
    }

    private string FormatDownloadStatus(UpdateProgressInfo progress)
    {
        if (progress.Progress.HasValue)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                GetString("About.Update.Progress.Downloading.WithPercent", "Downloading update package... {0:0}%"),
                progress.Progress.Value * 100d);
        }

        return GetString("About.Update.Progress.Downloading", "Downloading update package...");
    }

    private void ResetUpdateProgress()
    {
        IsUpdateProgressVisible = false;
        IsUpdateProgressIndeterminate = false;
        UpdateProgressValue = 0;
        UpdateProgressStatus = string.Empty;
    }

    private static string BuildReleaseNotesPreview(string releaseNotes)
    {
        if (string.IsNullOrWhiteSpace(releaseNotes))
        {
            return "No release notes.";
        }

        var lines = releaseNotes
            .Split('\n', StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(8)
            .ToArray();
        var preview = string.Join(Environment.NewLine, lines);
        return preview.Length <= 1200 ? preview : $"{preview[..1200]}...";
    }
}
