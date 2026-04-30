using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public partial class AboutTabViewModel : ViewModelBase
{
    private const string RepositoryUrl = "https://github.com/mehaifeng/AthenaAgent";
    private readonly ILogger _logger = Log.ForContext<AboutTabViewModel>();

    [RelayCommand]
    private async Task CheckForUpdatesAsync() { await Task.CompletedTask; }

    [RelayCommand]
    private void OpenDocumentation() { }

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
