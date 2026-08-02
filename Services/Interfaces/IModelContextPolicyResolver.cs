using Athena.UI.Models;

namespace Athena.UI.Services.Interfaces;

public interface IModelContextPolicyResolver
{
    ResolvedContextPolicy Resolve(
        ResolvedModelMetadata model,
        AppContextPolicy app,
        WorkspaceContextPolicyOverride? workspace,
        AiModelRole role);
}
