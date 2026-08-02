using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using System;
using System.Linq;

namespace Athena.UI.Services.Context;

/// <summary>Publishes next-request policy invalidation while preserving per-conversation usage state.</summary>
public sealed class ContextPolicyProvider : IContextPolicyProvider, IDisposable
{
    private readonly IConfigService _configService;
    private readonly IOpenRouterModelMetadataCatalog _catalog;
    private readonly IModelMetadataResolver _metadataResolver;
    private readonly IModelContextPolicyResolver _policyResolver;
    private readonly IWorkspaceService? _workspaceService;

    public ContextPolicyProvider(
        IConfigService configService,
        IOpenRouterModelMetadataCatalog catalog,
        IModelMetadataResolver metadataResolver,
        IModelContextPolicyResolver policyResolver,
        IWorkspaceService? workspaceService = null)
    {
        _configService = configService;
        _catalog = catalog;
        _metadataResolver = metadataResolver;
        _policyResolver = policyResolver;
        _workspaceService = workspaceService;
        _configService.ConfigChanged += OnConfigChanged;
        _catalog.CatalogChanged += OnChanged;
        if (_workspaceService != null) _workspaceService.WorkspacePolicyChanged += OnWorkspaceChanged;
    }

    public event EventHandler? EffectivePolicyChanged;

    public EffectiveContextPolicySnapshot? Resolve(WorkspaceContextPolicyOverride? workspaceOverride = null)
        => ResolveRole(AiModelRole.MainConversation, workspaceOverride);

    public EffectiveContextPolicySnapshot? ResolveRole(
        AiModelRole modelRole,
        WorkspaceContextPolicyOverride? workspaceOverride = null)
    {
        var config = _configService.Load();
        var role = modelRole switch
        {
            AiModelRole.MainConversation => config.AiModels.MainConversation,
            AiModelRole.TitleGeneration => config.AiModels.TitleGeneration,
            AiModelRole.ContextCompression => config.AiModels.ContextCompression,
            AiModelRole.Approval => config.AiModels.Approval,
            AiModelRole.Embedding => config.AiModels.Embedding,
            AiModelRole.BrowserAgent => config.AiModels.BrowserAgent,
            AiModelRole.SubAgent => config.AiModels.SubAgent,
            AiModelRole.KnowledgeMaintenance => config.AiModels.KnowledgeMaintenance,
            AiModelRole.ImageRecognition => config.AiModels.ImageRecognition,
            _ => throw new ArgumentOutOfRangeException(nameof(modelRole), modelRole, null)
        };
        var provider = config.AiModels.Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, role.ProviderId, StringComparison.Ordinal));
        if (provider == null || string.IsNullOrWhiteSpace(role.Model)) return null;
        var model = provider.Models.FirstOrDefault(candidate =>
                        string.Equals(candidate.Id, role.Model, StringComparison.Ordinal))
                    ?? new ProviderModelDescriptor
                    {
                        Id = role.Model,
                        DisplayName = role.Model,
                        IsManual = true
                    };
        var profile = config.AiModels.ModelMetadataProfiles.FirstOrDefault(candidate =>
            string.Equals(candidate.ProviderId, provider.Id, StringComparison.Ordinal)
            && string.Equals(candidate.ExternalModelId, role.Model, StringComparison.Ordinal));
        var metadata = _metadataResolver.Resolve(provider, model, profile, _catalog.Current, _catalog.IsStale);
        var policy = _policyResolver.Resolve(
            metadata,
            config.ContextPolicy,
            modelRole == AiModelRole.MainConversation ? workspaceOverride : null,
            modelRole);
        return new EffectiveContextPolicySnapshot(metadata, policy, _catalog.Current.CatalogRevision, provider.Id, role.Model);
    }

    private void OnChanged(object? sender, EventArgs args) => EffectivePolicyChanged?.Invoke(this, EventArgs.Empty);
    private void OnConfigChanged(object? sender, AppConfig config) => EffectivePolicyChanged?.Invoke(this, EventArgs.Empty);
    private void OnWorkspaceChanged(object? sender, string workspaceId) => EffectivePolicyChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _configService.ConfigChanged -= OnConfigChanged;
        _catalog.CatalogChanged -= OnChanged;
        if (_workspaceService != null) _workspaceService.WorkspacePolicyChanged -= OnWorkspaceChanged;
    }
}
