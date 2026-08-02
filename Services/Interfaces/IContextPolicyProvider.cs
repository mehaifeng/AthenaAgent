using Athena.UI.Models;
using System;

namespace Athena.UI.Services.Interfaces;

public sealed record EffectiveContextPolicySnapshot(
    ResolvedModelMetadata Metadata,
    ResolvedContextPolicy Policy,
    string CatalogRevision,
    string ProviderId,
    string ExternalModelId);

public interface IContextPolicyProvider
{
    event EventHandler? EffectivePolicyChanged;
    EffectiveContextPolicySnapshot? Resolve(WorkspaceContextPolicyOverride? workspaceOverride = null);
    EffectiveContextPolicySnapshot? ResolveRole(
        AiModelRole role,
        WorkspaceContextPolicyOverride? workspaceOverride = null);
}
