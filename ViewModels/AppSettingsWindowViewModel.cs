namespace Athena.UI.ViewModels;

public sealed class AppSettingsWindowViewModel : ViewModelBase, System.IDisposable
{
    public AppSettingsWindowViewModel(
        AppSettingsViewModel appSettings,
        AboutViewModel about)
    {
        AppSettings = appSettings;
        About = about;
        AppSettings.ActivateWindow();
    }

    public AppSettingsViewModel AppSettings { get; }

    public AboutViewModel About { get; }

    public void Dispose() => AppSettings.DeactivateWindow();
}
