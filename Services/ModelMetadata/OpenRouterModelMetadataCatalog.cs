using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.ModelMetadata;

public sealed class OpenRouterModelMetadataCatalog : IOpenRouterModelMetadataCatalog
{
    public const string SourceUrl = "https://openrouter.ai/api/v1/models?output_modalities=text";
    private const int MaxPages = 20;
    private const int MaxModels = 10_000;
    private const int MaxResponseBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    private readonly HttpClient _httpClient;
    private readonly OpenRouterModelMetadataStore _store;
    private readonly Func<string?> _apiKeyProvider;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _logger;
    private readonly object _singleFlightLock = new();
    private Task<ModelCatalogRefreshResult>? _refreshTask;
    private OpenRouterCatalogPointer _pointer;
    private readonly OpenRouterCatalogSnapshot _seed;
    private long _cacheGeneration;

    public OpenRouterModelMetadataCatalog(
        HttpClient httpClient,
        OpenRouterModelMetadataStore store,
        OpenRouterCatalogSnapshot seed,
        ILogger logger,
        Func<string?>? apiKeyProvider = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _httpClient = httpClient;
        _store = store;
        _apiKeyProvider = apiKeyProvider ?? (() => null);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
        _logger = logger.ForContext<OpenRouterModelMetadataCatalog>();
        _seed = seed;
        var loaded = store.Load(seed);
        Current = loaded.Snapshot;
        _pointer = loaded.Pointer;
    }

    public OpenRouterCatalogSnapshot Current { get; private set; }

    public bool IsStale => Current.FetchedAtUtc == DateTimeOffset.MinValue
        || _utcNow() - Current.FetchedAtUtc > StaleAfter;

    public event EventHandler? CatalogChanged;

    public Task<ModelCatalogRefreshResult> RefreshAsync(bool force, CancellationToken cancellationToken = default)
    {
        lock (_singleFlightLock)
        {
            if (_refreshTask != null) return _refreshTask;
            var task = RefreshCoreAsync(force, Volatile.Read(ref _cacheGeneration), cancellationToken);
            _refreshTask = task;
            _ = ReleaseWhenCompletedAsync(task);
            return task;
        }
    }

    private async Task ReleaseWhenCompletedAsync(Task<ModelCatalogRefreshResult> task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        finally
        {
            lock (_singleFlightLock)
            {
                if (ReferenceEquals(_refreshTask, task)) _refreshTask = null;
            }
        }
    }

    private async Task<ModelCatalogRefreshResult> RefreshCoreAsync(
        bool force,
        long cacheGeneration,
        CancellationToken cancellationToken)
    {
        if (!force && _pointer.LastCheckedAtUtc != DateTimeOffset.MinValue && _utcNow() - _pointer.LastCheckedAtUtc < Ttl)
            return new ModelCatalogRefreshResult(ModelCatalogRefreshStatus.SkippedFresh, "Catalog is fresh.", Current.Models.Count);

        try
        {
            var pages = new List<OpenRouterModelMetadata>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            Uri? next = new(SourceUrl);
            string? etag = null;
            int? totalCount = null;
            for (var page = 0; next != null; page++)
            {
                if (page >= MaxPages || !visited.Add(next.AbsoluteUri))
                    throw new InvalidDataException("OpenRouter pagination loop or page limit detected.");
                var response = await SendWithPolicyAsync(next, force, page == 0 ? _pointer.ETag : null, cancellationToken).ConfigureAwait(false);
                using (response)
                {
                    if (response.StatusCode == HttpStatusCode.NotModified && page == 0)
                    {
                        lock (_singleFlightLock)
                        {
                            if (cacheGeneration != Volatile.Read(ref _cacheGeneration))
                                return new ModelCatalogRefreshResult(ModelCatalogRefreshStatus.Cancelled, "Catalog cache changed during refresh.", Current.Models.Count);
                            _pointer = _store.Touch(_pointer, _utcNow(), response.Headers.ETag?.Tag);
                        }
                        return new ModelCatalogRefreshResult(ModelCatalogRefreshStatus.NotModified, "Catalog not modified.", Current.Models.Count);
                    }
                    response.EnsureSuccessStatusCode();
                    etag ??= response.Headers.ETag?.Tag;
                    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                    if (bytes.Length > MaxResponseBytes) throw new InvalidDataException("OpenRouter response exceeds size limit.");
                    using var document = JsonDocument.Parse(bytes);
                    var parsed = ParsePage(document.RootElement, out var nextValue, out var pageTotal);
                    pages.AddRange(parsed);
                    if (pages.Count > MaxModels) throw new InvalidDataException("OpenRouter model count exceeds limit.");
                    totalCount ??= pageTotal;
                    next = ResolveNext(nextValue);
                }
            }

            var unique = new Dictionary<string, OpenRouterModelMetadata>(StringComparer.Ordinal);
            foreach (var model in pages)
            {
                if (!unique.TryAdd(model.Id, model)) throw new InvalidDataException($"Duplicate OpenRouter model id: {model.Id}");
            }
            if (unique.Count == 0) throw new InvalidDataException("OpenRouter catalog is empty.");
            if (totalCount.HasValue && unique.Count > totalCount.Value)
                throw new InvalidDataException("OpenRouter total_count is inconsistent.");
            if (Current.Models.Count >= 20 && unique.Count < Current.Models.Count / 2
                && (!totalCount.HasValue || totalCount.Value >= Current.Models.Count / 2))
            {
                _logger.Warning("OpenRouter 元数据异常缩水进入 quarantine: Old={Old}, New={New}", Current.Models.Count, unique.Count);
                return new ModelCatalogRefreshResult(ModelCatalogRefreshStatus.Quarantined, "Catalog shrank unexpectedly.", unique.Count);
            }

            var models = unique.Values.OrderBy(model => model.Id, StringComparer.Ordinal).ToList();
            var hash = OpenRouterModelMetadataStore.ComputeContentHash(models);
            var snapshot = new OpenRouterCatalogSnapshot(1, hash, _utcNow(), SourceUrl, hash, etag, models);
            lock (_singleFlightLock)
            {
                if (cacheGeneration != Volatile.Read(ref _cacheGeneration))
                    return new ModelCatalogRefreshResult(ModelCatalogRefreshStatus.Cancelled, "Catalog cache changed during refresh.", Current.Models.Count);
                _pointer = _store.Commit(snapshot, _pointer);
                Current = snapshot;
            }
            CatalogChanged?.Invoke(this, EventArgs.Empty);
            return new ModelCatalogRefreshResult(ModelCatalogRefreshStatus.Succeeded, "Catalog refreshed.", models.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ModelCatalogRefreshResult(ModelCatalogRefreshStatus.Cancelled, "Catalog refresh cancelled.", Current.Models.Count);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "OpenRouter 元数据刷新失败，继续使用 last-known-good");
            return new ModelCatalogRefreshResult(ModelCatalogRefreshStatus.Failed, ex.Message, Current.Models.Count, ex);
        }
    }

    public Task ClearLocalCacheAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_singleFlightLock)
        {
            Interlocked.Increment(ref _cacheGeneration);
            _store.Clear();
            Current = _seed;
            _pointer = new OpenRouterCatalogPointer(1, null, null, null, DateTimeOffset.MinValue);
        }
        CatalogChanged?.Invoke(this, EventArgs.Empty);
        _logger.Information("ModelCatalogRefresh/CacheCleared Models={ModelCount}", Current.Models.Count);
        return Task.CompletedTask;
    }

    private async Task<HttpResponseMessage> SendWithPolicyAsync(
        Uri uri,
        bool manual,
        string? etag,
        CancellationToken cancellationToken)
    {
        var maxAttempts = manual ? 1 : 3;
        var apiKey = _apiKeyProvider();
        var authenticatedRetryUsed = false;
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrWhiteSpace(etag)) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            if (authenticatedRetryUsed && !string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                && !authenticatedRetryUsed && !string.IsNullOrWhiteSpace(apiKey))
            {
                response.Dispose();
                authenticatedRetryUsed = true;
                continue;
            }
            if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                && attempt < maxAttempts)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt - 1));
                response.Dispose();
                await _delay(retryAfter, cancellationToken).ConfigureAwait(false);
                continue;
            }
            return response;
        }
    }

    internal static IReadOnlyList<OpenRouterModelMetadata> ParsePage(
        JsonElement root,
        out string? next,
        out int? totalCount)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("OpenRouter root data must be an array.");
        totalCount = root.TryGetProperty("total_count", out var total) && total.TryGetInt32(out var count) ? count : null;
        next = null;
        if (root.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Object
            && links.TryGetProperty("next", out var nextElement) && nextElement.ValueKind == JsonValueKind.String)
            next = nextElement.GetString();

        var result = new List<OpenRouterModelMetadata>();
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(idElement.GetString())) continue;
            var architecture = item.TryGetProperty("architecture", out var architectureElement) && architectureElement.ValueKind == JsonValueKind.Object
                ? architectureElement : default;
            var outputs = ReadStringSet(architecture, "output_modalities");
            if (!outputs.Contains("text")) continue;
            var context = ReadPositiveInt64(item, "context_length");
            var topProviderElement = item.TryGetProperty("top_provider", out var top) && top.ValueKind == JsonValueKind.Object ? top : default;
            var topContext = ReadPositiveInt64(topProviderElement, "context_length");
            var maxCompletion = ReadPositiveInt64(topProviderElement, "max_completion_tokens");
            result.Add(new OpenRouterModelMetadata(
                idElement.GetString()!,
                ReadString(item, "canonical_slug"),
                ReadString(item, "name") ?? idElement.GetString()!,
                ReadInt64(item, "created"),
                ReadString(item, "description"),
                context,
                new OpenRouterArchitecture(
                    ReadStringSet(architecture, "input_modalities"),
                    outputs,
                    ReadString(architecture, "tokenizer"),
                    ReadString(architecture, "instruct_type")),
                topProviderElement.ValueKind == JsonValueKind.Object ? new OpenRouterTopProvider(topContext, maxCompletion) : null,
                null,
                ReadStringSet(item, "supported_parameters"),
                null,
                ReadDate(item, "expiration_date"),
                item.Clone()));
        }
        return result;
    }

    private static Uri? ResolveNext(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(new Uri("https://openrouter.ai"), value, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase)
                 || uri.Host.EndsWith(".openrouter.ai", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("OpenRouter pagination next URL is not allowlisted.");
        return uri;
    }

    private static HashSet<string> ReadStringSet(JsonElement element, string property)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) return result;
        foreach (var value in array.EnumerateArray()) if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text) result.Add(text);
        return result;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? ReadInt64(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetInt64(out var number) ? number : null;

    private static long? ReadPositiveInt64(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (!value.TryGetInt64(out var number) || number <= 0) throw new InvalidDataException($"OpenRouter {property} must be a positive Int64.");
        return number;
    }

    private static DateTimeOffset? ReadDate(JsonElement element, string property) =>
        ReadString(element, property) is { } text && DateTimeOffset.TryParse(text, out var value) ? value : null;
}
