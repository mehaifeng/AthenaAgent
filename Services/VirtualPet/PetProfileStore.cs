using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.VirtualPet;

/// <summary>
/// pet_profile.json 的原子读写。
///
/// 养成存档是纯娱乐数据，任何一条读不出来都不该影响应用启动：整份文件坏了就隔离
/// 并从空档重来，单条记录坏了只丢那一条。写入走临时文件 + 覆盖移动，避免断电留下半个文件。
/// </summary>
public sealed class PetProfileStore : IPetProfileStore, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public PetProfileStore(IPlatformPathService pathService, ILogger logger)
    {
        _filePath = pathService.GetPetProfileFilePath();
        _logger = logger.ForContext<PetProfileStore>();

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }

    public VirtualPetProfileDocument Load()
    {
        if (!File.Exists(_filePath))
        {
            _logger.Debug("No pet profile at {Path}; starting fresh", _filePath);
            return new VirtualPetProfileDocument();
        }

        string json;
        try
        {
            json = File.ReadAllText(_filePath);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to read the pet profile at {Path}; starting fresh", _filePath);
            return new VirtualPetProfileDocument();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            _logger.Warning(ex, "Pet profile at {Path} is not valid JSON; quarantining it", _filePath);
            Quarantine();
            return new VirtualPetProfileDocument();
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("pets", out var petsElement)
                || petsElement.ValueKind != JsonValueKind.Array)
            {
                _logger.Warning("Pet profile at {Path} has no 'pets' array; quarantining it", _filePath);
                Quarantine();
                return new VirtualPetProfileDocument();
            }

            if (document.RootElement.TryGetProperty("schemaVersion", out var versionElement)
                && versionElement.TryGetInt32(out var version)
                && version != VirtualPetProgressionRules.CurrentSchemaVersion)
            {
                _logger.Information(
                    "Pet profile schema version {Found} differs from {Expected}; reading best-effort",
                    version, VirtualPetProgressionRules.CurrentSchemaVersion);
            }

            var pets = new List<VirtualPetCompanionRecord>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var corrupted = 0;

            foreach (var element in petsElement.EnumerateArray())
            {
                try
                {
                    var record = element.Deserialize<VirtualPetCompanionRecord>(SerializerOptions);
                    if (record == null || string.IsNullOrWhiteSpace(record.Slug) || !seen.Add(record.Slug))
                    {
                        corrupted++;
                        continue;
                    }
                    pets.Add(record);
                }
                catch (Exception ex)
                {
                    corrupted++;
                    _logger.Debug(ex, "Skipped a corrupted pet record");
                }
            }

            if (corrupted > 0)
                _logger.Warning("Loaded {Count} pet record(s); isolated {Corrupted} unreadable one(s)", pets.Count, corrupted);

            return new VirtualPetProfileDocument
            {
                SchemaVersion = VirtualPetProgressionRules.CurrentSchemaVersion,
                Pets = pets
            };
        }
    }

    public async Task SaveAsync(VirtualPetProfileDocument document)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            document.SchemaVersion = VirtualPetProgressionRules.CurrentSchemaVersion;
            var json = JsonSerializer.Serialize(document, SerializerOptions);
            var temporaryPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, json).ConfigureAwait(false);
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to persist the pet profile to {Path}", _filePath);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void Quarantine()
    {
        try
        {
            File.Move(_filePath, _filePath + ".corrupt", overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to quarantine the unreadable pet profile");
        }
    }

    public void Dispose() => _writeGate.Dispose();
}
