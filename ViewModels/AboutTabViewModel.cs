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

namespace Athena.UI.ViewModels;

public partial class AboutTabViewModel : ViewModelBase
{
    private const string RepositoryUrl = "https://github.com/mehaifeng/AthenaAgent";
    private const string EnglishGuideUrl = "https://github.com/mehaifeng/AthenaAgent/blob/main/Docs/Athena_User_Guide_EN.md";
    private const string ChineseGuideUrl = "https://github.com/mehaifeng/AthenaAgent/blob/main/Docs/Athena_User_Guide_CN.md";
    private readonly ILocalizationService? _localizationService;
    private readonly IUpdateService? _updateService;
    private readonly ILogger _logger = Log.ForContext<AboutTabViewModel>();

    [ObservableProperty]
    private bool _isCheckingUpdates;

    [ObservableProperty]
    private string _currentVersion;

    public bool CanCheckForUpdates => !IsCheckingUpdates;

    public AboutTabViewModel() : this(null, null) { }

    public AboutTabViewModel(ILocalizationService? localizationService, IUpdateService? updateService = null)
    {
        _localizationService = localizationService;
        _updateService = updateService;
        _currentVersion = _updateService?.GetCurrentVersion() ?? "0.0.0";
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

            var applyResult = await _updateService.PrepareAndLaunchUpdateAsync(checkResult);
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
