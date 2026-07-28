namespace Athena.UI.ViewModels;

public sealed class AgentRuntimeSettingsViewModel(AppSettingsState state) : ViewModelBase
{
    public AppSettingsState State { get; } = state;
}
