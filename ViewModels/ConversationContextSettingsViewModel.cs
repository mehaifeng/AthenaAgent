namespace Athena.UI.ViewModels;

public sealed class ConversationContextSettingsViewModel(AppSettingsState state) : ViewModelBase
{
    public AppSettingsState State { get; } = state;
}
