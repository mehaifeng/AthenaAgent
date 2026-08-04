using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Athena.UI.Services.ModelMetadata;

public sealed class OpenRouterModelMetadataStore
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _root;
    private readonly string _snapshots;
    private readonly string _pointerPath;
    private readonly ILogger _logger;

    public OpenRouterModelMetadataStore(IPlatformPathService paths, ILogger logger)
    {
        _root = Path.Combine(paths.GetModelMetadataDirectory(), "OpenRouter");
        _snapshots = Path.Combine(_root, "snapshots");
        _pointerPath = Path.Combine(_root, "current.json");
        _logger = logger.ForContext<OpenRouterModelMetadataStore>();
    }

    public (OpenRouterCatalogSnapshot Snapshot, OpenRouterCatalogPointer Pointer) Load(OpenRouterCatalogSnapshot seed)
    {
        Directory.CreateDirectory(_snapshots);
        var pointer = ReadPointer();
        if (pointer != null)
        {
            var current = ReadSnapshot(pointer.CurrentRevision);
            if (current != null) return (current, pointer);
            var previous = ReadSnapshot(pointer.PreviousRevision);
            if (previous != null)
            {
                _logger.Warning("OpenRouter Current snapshot corrupted; falling back to Previous: {Revision}", previous.CatalogRevision);
                return (previous, pointer with { CurrentRevision = previous.CatalogRevision });
            }
        }

        var scanned = Directory.EnumerateFiles(_snapshots, "*.json")
            .Select(path => ReadSnapshot(Path.GetFileNameWithoutExtension(path)))
            .Where(snapshot => snapshot != null)
            .Cast<OpenRouterCatalogSnapshot>()
            .OrderByDescending(snapshot => snapshot.FetchedAtUtc)
            .FirstOrDefault();
        if (scanned != null)
        {
            var recovered = new OpenRouterCatalogPointer(SchemaVersion, scanned.CatalogRevision, null, scanned.ETag, DateTimeOffset.MinValue);
            WritePointer(recovered);
            return (scanned, recovered);
        }

        return (seed, new OpenRouterCatalogPointer(SchemaVersion, null, null, null, DateTimeOffset.MinValue));
    }

    public OpenRouterCatalogPointer Commit(OpenRouterCatalogSnapshot snapshot, OpenRouterCatalogPointer current)
    {
        Directory.CreateDirectory(_snapshots);
        ValidateSnapshot(snapshot);
        var path = Path.Combine(_snapshots, snapshot.CatalogRevision + ".json");
        if (!File.Exists(path)) WriteAtomic(path, JsonSerializer.Serialize(snapshot, JsonOptions));
        var next = new OpenRouterCatalogPointer(
            SchemaVersion,
            snapshot.CatalogRevision,
            current.CurrentRevision,
            snapshot.ETag,
            DateTimeOffset.UtcNow);
        WritePointer(next);
        return next;
    }

    public OpenRouterCatalogPointer Touch(OpenRouterCatalogPointer pointer, DateTimeOffset checkedAt, string? etag)
    {
        var next = pointer with { LastCheckedAtUtc = checkedAt, ETag = etag ?? pointer.ETag };
        WritePointer(next);
        return next;
    }

    public void Clear()
    {
        if (!Directory.Exists(_root)) return;
        var tombstone = _root + ".clearing-" + Guid.NewGuid().ToString("N");
        Directory.Move(_root, tombstone);
        Directory.CreateDirectory(_snapshots);
        try
        {
            Directory.Delete(tombstone, recursive: true);
        }
        catch (Exception ex)
        {
            // The active cache is already atomically detached. A leftover tombstone
            // is inert and may be removed on the next maintenance pass.
            _logger.Warning(ex, "Detached OpenRouter cache tombstone could not be deleted: {Path}", tombstone);
        }
    }

    public static string ComputeContentHash(IReadOnlyList<OpenRouterModelMetadata> models)
    {
        var json = JsonSerializer.Serialize(models, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private OpenRouterCatalogPointer? ReadPointer()
    {
        try
        {
            if (!File.Exists(_pointerPath)) return null;
            var pointer = JsonSerializer.Deserialize<OpenRouterCatalogPointer>(File.ReadAllText(_pointerPath), JsonOptions);
            return pointer?.SchemaVersion == SchemaVersion ? pointer : null;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "OpenRouter pointer corrupted; will scan immutable snapshots");
            return null;
        }
    }

    private OpenRouterCatalogSnapshot? ReadSnapshot(string? revision)
    {
        if (string.IsNullOrWhiteSpace(revision)) return null;
        try
        {
            var path = Path.Combine(_snapshots, revision + ".json");
            if (!File.Exists(path)) return null;
            var snapshot = JsonSerializer.Deserialize<OpenRouterCatalogSnapshot>(File.ReadAllText(path), JsonOptions);
            if (snapshot == null) return null;
            ValidateSnapshot(snapshot);
            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "OpenRouter snapshot validation failed: {Revision}", revision);
            return null;
        }
    }

    private static void ValidateSnapshot(OpenRouterCatalogSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != SchemaVersion || snapshot.Models.Count == 0)
            throw new InvalidDataException("OpenRouter snapshot schema or data is invalid.");
        var hash = ComputeContentHash(snapshot.Models);
        if (!string.Equals(hash, snapshot.ContentHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(hash, snapshot.CatalogRevision, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("OpenRouter snapshot hash is invalid.");
    }

    private void WritePointer(OpenRouterCatalogPointer pointer) =>
        WriteAtomic(_pointerPath, JsonSerializer.Serialize(pointer, JsonOptions));

    private static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
