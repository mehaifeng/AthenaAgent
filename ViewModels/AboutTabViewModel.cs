using CommunityToolkit.Mvvm.Input;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public partial class AboutTabViewModel : ViewModelBase
{
    [RelayCommand]
    private async Task CheckForUpdatesAsync() { await Task.CompletedTask; }

    [RelayCommand]
    private void OpenDocumentation() { }

    [RelayCommand]
    private void OpenGitHub() { }
}
