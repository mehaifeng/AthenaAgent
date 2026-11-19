using CommunityToolkit.Mvvm.Input;
using Serilog;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public sealed partial class ToolApprovalSettingsViewModel : ViewModelBase
{
    private readonly ILogger? _logger;

    public ToolApprovalSettingsViewModel(
        AppSettingsState state,
        ToolApprovalModePickerViewModel modePicker,
        ILogger? logger = null)
    {
        State = state;
        ModePicker = modePicker;
        _logger = logger;
    }

    public AppSettingsState State { get; }
    public ToolApprovalModePickerViewModel ModePicker { get; }

    [RelayCommand]
    private async Task RevokeAutoAllowedToolAsync(string? tool)
    {
        if (!string.IsNullOrEmpty(tool) && State.Config.AutoAllowedTools.Remove(tool))
        {
            _logger?.Information("ToolApprovalSettings user revoked auto-allowed tool: Tool={Tool}", tool);
            await State.SaveNowAsync();
        }
    }

    [RelayCommand]
    private async Task RevokeTerminalAllowlistAsync(string? command)
    {
        if (!string.IsNullOrEmpty(command) && State.Config.TerminalAllowlist.Remove(command))
        {
            _logger?.Information("ToolApprovalSettings user revoked terminal allowlist entry: Command={Command}", command);
            await State.SaveNowAsync();
        }
    }
}
