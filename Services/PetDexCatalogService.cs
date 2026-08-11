using Athena.UI.Services.Interfaces;
using Avalonia.Media.Imaging;
using Serilog;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>
/// Loads the PetDex manifest and installs selected packages into AthenaData/Pets.
/// Asset downloads are pinned to PetDex hosts, bounded, validated, and atomically published.
/// </summary>
public sealed class PetDexCatalogService : IPetDexCatalogService, IDisposable
{
    public const string ManifestUrl = "https://petdex.dev/api/manifest";
    private const int ManifestLimitBytes = 5 * 1024 * 1024;
    private const int SpriteLimitBytes = 12 * 1024 * 1024;
    private const int MetadataLimitBytes = 256 * 1024;
    private static readonly TimeSpan CatalogLifetime = TimeSpan.FromMinutes(15);

    private readonly HttpClient _httpClient;
    private readonly string _petRoot;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private readonly SemaphoreSlim _thumbnailGate = new(4, 4);
    private readonly ConcurrentDictionary<string, Lazy<Task<Bitmap?>>> _thumbnailCache =
        new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<PetDexCatalogEntry>? _cachedCatalog;
    private DateTime _catalogExpiresAt;

    public PetDexCatalogService(IPlatformPathService pathService, HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _petRoot = Path.Combine(pathService.GetAppDataDirectory(), "Pets");
        Directory.CreateDirectory(_petRoot);
        PetDexPetLibrary.ConfigureInstalledRoot(_petRoot);
    }

    public IReadOnlyList<PetDexCatalogEntry> GetLocalCatalog()
    {
        var entries = PetDexPetLibrary.BuiltIns
            .Select(pet => new PetDexCatalogEntry(
                pet.Slug,
                pet.DisplayName,
                pet.Kind,
                pet.SubmittedBy,
                string.Empty,
                string.Empty,
                IsBuiltIn: true,
                IsInstalled: true,
                IsCurated: true))
            .ToList();

        if (!Directory.Exists(_petRoot)) return entries;
        foreach (var directory in Directory.EnumerateDirectories(_petRoot))
        {
            var slug = Path.GetFileName(directory);
            if (entries.Any(entry => entry.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase)))
                continue;
            var metadataPath = Path.Combine(directory, "pet.json");
            if (!File.Exists(metadataPath) || !PetDexPetLibrary.TryResolveExact(slug, out _)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
                var root = document.RootElement;
                entries.Add(new PetDexCatalogEntry(
                    slug,
                    JsonString(root, "displayName") ?? slug,
                    JsonString(root, "kind") ?? "pet",
                    JsonString(root, "submittedBy") ?? string.Empty,
                    JsonString(root, "sourceSpritesheetUrl") ?? string.Empty,
                    JsonString(root, "sourcePetJsonUrl") ?? string.Empty,
                    IsBuiltIn: false,
                    IsInstalled: true,
                    IsCurated: false));
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Ignoring unreadable PetDex package {Slug}", slug);
            }
        }
        return entries;
    }

    public async Task<IReadOnlyList<PetDexCatalogEntry>> GetCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        if (_cachedCatalog is not null && DateTime.UtcNow < _catalogExpiresAt)
            return _cachedCatalog;
        await _catalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedCatalog is not null && DateTime.UtcNow < _catalogExpiresAt)
                return _cachedCatalog;
            using var response = await _httpClient.GetAsync(
                ManifestUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var payload = await ReadBoundedAsync(response, ManifestLimitBytes, cancellationToken).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize<PetDexManifest>(payload, JsonOptions)
                           ?? throw new InvalidDataException("PetDex manifest is empty.");

            var local = GetLocalCatalog();
            var bySlug = local.ToDictionary(entry => entry.Slug, StringComparer.OrdinalIgnoreCase);
            foreach (var pet in manifest.Pets ?? [])
            {
                var slug = SafeSlug(pet.Slug);
                if (slug is null || string.IsNullOrWhiteSpace(pet.SpritesheetUrl)) continue;
                if (bySlug.TryGetValue(slug, out var installed))
                {
                    bySlug[slug] = installed with
                    {
                        SpritesheetUrl = pet.SpritesheetUrl,
                        PetJsonUrl = pet.PetJsonUrl ?? string.Empty,
                        IsCurated = pet.SpritesheetUrl.Contains("/curated/", StringComparison.OrdinalIgnoreCase)
                    };
                }
                else
                {
                    bySlug[slug] = new PetDexCatalogEntry(
                        slug,
                        pet.DisplayName ?? slug,
                        pet.Kind ?? "pet",
                        pet.SubmittedBy ?? string.Empty,
                        pet.SpritesheetUrl,
                        pet.PetJsonUrl ?? string.Empty,
                        IsBuiltIn: false,
                        IsInstalled: false,
                        IsCurated: pet.SpritesheetUrl.Contains("/curated/", StringComparison.OrdinalIgnoreCase));
                }
            }

            _cachedCatalog = bySlug.Values.ToArray();
            _catalogExpiresAt = DateTime.UtcNow + CatalogLifetime;
            return _cachedCatalog;
        }
        finally
        {
            _catalogGate.Release();
        }
    }

    public async Task InstallAsync(
        PetDexCatalogEntry entry,
        CancellationToken cancellationToken = default)
    {
        if (entry.IsInstalled || PetDexPetLibrary.IsInstalled(entry.Slug)) return;
        var slug = SafeSlug(entry.Slug) ?? throw new InvalidDataException("Invalid PetDex slug.");
        var spriteUri = RequirePetDexAssetUri(entry.SpritesheetUrl);
        var metadataUri = string.IsNullOrWhiteSpace(entry.PetJsonUrl)
            ? null
            : RequirePetDexAssetUri(entry.PetJsonUrl);

        var target = Path.Combine(_petRoot, slug);
        if (Directory.Exists(target))
            throw new InvalidDataException($"PetDex package directory '{slug}' already exists but is not usable.");
        var temporary = Path.Combine(_petRoot, $".{slug}-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(temporary);
        try
        {
            var extension = spriteUri.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? ".png"
                : ".webp";
            var spriteName = "spritesheet" + extension;
            var spriteBytes = await DownloadBoundedAsync(
                spriteUri,
                SpriteLimitBytes,
                cancellationToken).ConfigureAwait(false);
            var spritePath = Path.Combine(temporary, spriteName);
            await File.WriteAllBytesAsync(spritePath, spriteBytes, cancellationToken).ConfigureAwait(false);
            ValidateSpriteGeometry(spritePath);

            var metadata = await TryDownloadMetadataAsync(metadataUri, cancellationToken).ConfigureAwait(false);
            metadata["id"] = slug;
            metadata["displayName"] = entry.DisplayName;
            metadata["spritesheetPath"] = spriteName;
            metadata["kind"] = entry.Kind;
            metadata["submittedBy"] = entry.SubmittedBy;
            metadata["sourceSpritesheetUrl"] = entry.SpritesheetUrl;
            metadata["sourcePetJsonUrl"] = entry.PetJsonUrl;
            await File.WriteAllTextAsync(
                Path.Combine(temporary, "pet.json"),
                JsonSerializer.Serialize(metadata, PrettyJsonOptions),
                cancellationToken).ConfigureAwait(false);

            Directory.Move(temporary, target);
            PetDexPetLibrary.Invalidate(slug);
            if (!PetDexPetLibrary.TryResolveExact(slug, out _))
                throw new InvalidDataException($"Installed PetDex package '{slug}' could not be loaded.");
        }
        catch
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
            throw;
        }
    }

    public async Task<Bitmap?> GetThumbnailAsync(
        PetDexCatalogEntry entry,
        CancellationToken cancellationToken = default)
    {
        if (entry.IsInstalled || string.IsNullOrWhiteSpace(entry.SpritesheetUrl))
            return null;
        var pending = _thumbnailCache.GetOrAdd(
            entry.Slug,
            _ => new Lazy<Task<Bitmap?>>(
                () => CreateThumbnailAsync(entry, cancellationToken),
                isThreadSafe: true));
        try
        {
            return await pending.Value.ConfigureAwait(false);
        }
        catch
        {
            _thumbnailCache.TryRemove(entry.Slug, out _);
            throw;
        }
    }

    private async Task<Bitmap?> CreateThumbnailAsync(PetDexCatalogEntry entry, CancellationToken cancellationToken)
    {
        await _thumbnailGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = await DownloadBoundedAsync(
                RequirePetDexAssetUri(entry.SpritesheetUrl),
                SpriteLimitBytes,
                cancellationToken).ConfigureAwait(false);
            using var source = SKBitmap.Decode(bytes);
            if (source is null
                || source.Width < PetDexPetLibrary.StandardFrameWidth
                || source.Height < PetDexPetLibrary.StandardFrameHeight)
                return null;
            using var frame = new SKBitmap(
                PetDexPetLibrary.StandardFrameWidth,
                PetDexPetLibrary.StandardFrameHeight,
                SKColorType.Rgba8888,
                SKAlphaType.Premul);
            if (!source.ExtractSubset(frame, new SKRectI(
                    0,
                    0,
                    PetDexPetLibrary.StandardFrameWidth,
                    PetDexPetLibrary.StandardFrameHeight)))
                return null;
            using var resized = frame.Resize(new SKImageInfo(58, 63), new SKSamplingOptions(SKFilterMode.Nearest));
            if (resized is null) return null;
            using var image = SKImage.FromBitmap(resized);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = new MemoryStream(encoded.ToArray(), writable: false);
            return new Bitmap(stream);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Could not load PetDex thumbnail {Slug}", entry.Slug);
            return null;
        }
        finally
        {
            _thumbnailGate.Release();
        }
    }

    private async Task<Dictionary<string, object?>> TryDownloadMetadataAsync(
        Uri? uri,
        CancellationToken cancellationToken)
    {
        if (uri is null) return [];
        try
        {
            var bytes = await DownloadBoundedAsync(uri, MetadataLimitBytes, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(bytes, JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Debug(ex, "PetDex metadata download failed; synthesizing pet.json");
            return [];
        }
    }

    private async Task<byte[]> DownloadBoundedAsync(Uri uri, int limit, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadBoundedAsync(response, limit, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpResponseMessage response,
        int limit,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > 0
            && response.Content.Headers.ContentLength > limit)
            throw new InvalidDataException("PetDex response exceeded the allowed size.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (destination.Length + read > limit)
                throw new InvalidDataException("PetDex response exceeded the allowed size.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return destination.ToArray();
    }

    private static Uri RequirePetDexAssetUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("assets.petdex.dev", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("PetDex assets must use https://assets.petdex.dev.");
        return uri;
    }

    private static void ValidateSpriteGeometry(string path)
    {
        using var bitmap = new Bitmap(path);
        var width = bitmap.PixelSize.Width;
        var height = bitmap.PixelSize.Height;
        if (width < PetDexPetLibrary.StandardFrameWidth * PetDexPetLibrary.StandardFramesPerState
            || width % PetDexPetLibrary.StandardFrameWidth != 0
            || height % PetDexPetLibrary.StandardFrameHeight != 0
            || height / PetDexPetLibrary.StandardFrameHeight < 8)
            throw new InvalidDataException($"Unsupported PetDex spritesheet geometry {width}x{height}.");
    }

    private static string? SafeSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        var trimmed = slug.Trim();
        return trimmed.Length <= 100
               && trimmed.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-')
            ? trimmed
            : null;
    }

    private static string? JsonString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public void Dispose()
    {
        foreach (var lazy in _thumbnailCache.Values)
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
                lazy.Value.Result?.Dispose();
        }
        _thumbnailCache.Clear();
        _catalogGate.Dispose();
        _thumbnailGate.Dispose();
    }

    private sealed class PetDexManifest
    {
        public PetDexManifestPet[]? Pets { get; set; }
    }

    private sealed class PetDexManifestPet
    {
        public string? Slug { get; set; }
        public string? DisplayName { get; set; }
        public string? Kind { get; set; }
        public string? SubmittedBy { get; set; }
        public string SpritesheetUrl { get; set; } = string.Empty;
        public string? PetJsonUrl { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true
    };
}
