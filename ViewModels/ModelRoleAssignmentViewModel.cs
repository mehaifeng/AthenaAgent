using Athena.UI.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace Athena.UI.ViewModels;

/// <summary>配置页中的一个业务模型角色；连接信息由统一的单一供应商配置提供。</summary>
public partial class ModelRoleAssignmentViewModel : ObservableObject
{
    public AiModelRole Role { get; }
    public string DisplayName { get; }
    public ModelRoleSettings Settings { get; }
    public ObservableCollection<string> ModelOptions { get; } = new();

    public ModelRoleAssignmentViewModel(
        AiModelRole role,
        string displayName,
        ModelRoleSettings settings)
    {
        Role = role;
        DisplayName = displayName;
        Settings = settings;
    }

    public bool SupportsOutputTokens => Role != AiModelRole.Embedding;

}
