using CommunityToolkit.Mvvm.Input;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public partial class AboutTabViewModel : ViewModelBase
{
    private const string RepositoryUrl = "https://github.com/mehaifeng/AthenaAgent";
    private const string EnglishGuideUrl = "https://github.com/mehaifeng/AthenaAgent/blob/main/Docs/Athena_User_Guide_EN.md";
    private const string ChineseGuideUrl = "https://github.com/mehaifeng/AthenaAgent/blob/main/Docs/Athena_User_Guide_CN.md";
    private readonly ILocalizationService? _localizationService;
    private readonly ILogger _logger = Log.ForContext<AboutTabViewModel>();

    public AboutTabViewModel() : this(null) { }

    public AboutTabViewModel(ILocalizationService? localizationService)
    {
        _localizationService = localizationService;
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync() { await Task.CompletedTask; }

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
}
