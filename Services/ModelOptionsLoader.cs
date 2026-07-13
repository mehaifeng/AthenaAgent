using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Athena.UI.Services.Interfaces;

namespace Athena.UI.Services;

/// <summary>
/// 「拉取模型列表」的共用流程：调用目录服务、过滤、回填候选集合并生成状态文案。
/// 加载中标志与防重入由调用方（各 ViewModel）自持。
/// </summary>
public static class ModelOptionsLoader
{
    public static async Task LoadAsync(
        IModelCatalogService? catalog,
        string? baseUrl,
        string? apiKey,
        ObservableCollection<string> options,
        Action<string> setStatus,
        Func<string, string, string> getString,
        Func<string, bool>? filter = null,
        Func<IModelCatalogService, string?, string?, Task<ModelCatalogResult>>? fetch = null)
    {
        if (catalog == null)
        {
            setStatus(getString("Status.ServiceNotInitialized", "Service not initialized"));
            return;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            setStatus(getString("Status.EnterApiKeyFirst", "Please enter API Key first"));
            return;
        }

        setStatus(getString("Status.LoadingModels", "Loading models..."));
        try
        {
            var result = fetch != null
                ? await fetch(catalog, baseUrl, apiKey)
                : await catalog.GetModelsAsync(baseUrl, apiKey);
            if (!result.Success)
            {
                setStatus(string.Format(
                    getString("Status.LoadModelsFailed", "Failed to load models: {0}"),
                    result.ErrorMessage));
                return;
            }

            var models = filter == null ? result.Models.ToList() : result.Models.Where(filter).ToList();

            options.Clear();
            foreach (var model in models)
            {
                options.Add(model);
            }

            setStatus(models.Count == 0
                ? getString("Status.NoModelsReturned", "Endpoint returned no models")
                : string.Format(getString("Status.ModelsLoaded", "Loaded {0} models"), models.Count));
        }
        catch (OperationCanceledException)
        {
            // 页面切换等外部取消，无需提示。
        }
        catch (Exception ex)
        {
            setStatus(string.Format(
                getString("Status.LoadModelsFailed", "Failed to load models: {0}"),
                ex.Message));
        }
    }
}
