namespace Athena.UI.ViewModels;

public sealed class GeneralSettingsViewModel(AppSettingsState state) : ViewModelBase
{
    public AppSettingsState State { get; } = state;
}
