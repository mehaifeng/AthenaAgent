namespace Athena.UI.ViewModels;

public sealed class AppSettingsWindowViewModel : ViewModelBase
{
    public AppSettingsWindowViewModel(
        AppSettingsViewModel appSettings,
        AboutViewModel about)
    {
        AppSettings = appSettings;
        About = about;
    }

    public AppSettingsViewModel AppSettings { get; }

    public AboutViewModel About { get; }
}
