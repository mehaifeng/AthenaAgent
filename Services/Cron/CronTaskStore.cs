using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Cron;

/// <summary>
/// cron_tasks.json 的原子读写。
///
/// 损坏隔离是这里的核心职责：任务列表先按 JsonElement 逐条读出，某一条解析失败只丢那一条并计数，
/// 绝不让一条脏记录把整个应用的定时能力打掉。整个文件不可解析时退回空列表并保留原文件副本。
/// </summary>
public sealed class CronTaskStore : ICronTaskStore, IDisposable
{
    /// <summary>存储 schema 版本。变更结构时递增，加载端据此决定如何解读。</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private readonly string _legacyFilePath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public CronTaskStore(IPlatformPathService pathService, ILogger logger)
    {
        _filePath = pathService.GetCronTasksFilePath();
        _legacyFilePath = pathService.GetLegacyScheduledTasksFilePath();
        _logger = logger.ForContext<CronTaskStore>();

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }

    public CronTaskLoadResult Load()
    {
        if (!File.Exists(_filePath))
        {
            _logger.Debug("No cron task store at {Path}; starting empty", _filePath);
            return new CronTaskLoadResult();
        }

        string json;
        try
        {
            json = File.ReadAllText(_filePath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to read cron task store at {Path}", _filePath);
            return new CronTaskLoadResult { FileUnreadable = true };
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "Cron task store at {Path} is not valid JSON; quarantining it and starting empty", _filePath);
            QuarantineFile();
            return new CronTaskLoadResult { FileUnreadable = true };
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("tasks", out var tasksElement)
                || tasksElement.ValueKind != JsonValueKind.Array)
            {
                _logger.Error("Cron task store at {Path} has no 'tasks' array; quarantining it and starting empty", _filePath);
                QuarantineFile();
                return new CronTaskLoadResult { FileUnreadable = true };
            }

            if (document.RootElement.TryGetProperty("schemaVersion", out var versionElement)
                && versionElement.TryGetInt32(out var version)
                && version != CurrentSchemaVersion)
            {
                _logger.Warning(
                    "Cron task store schema version {Found} differs from expected {Expected}; reading best-effort",
                    version, CurrentSchemaVersion);
            }

            var tasks = new List<CronTask>();
            var corrupted = 0;
            var seenIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var element in tasksElement.EnumerateArray())
            {
                try
                {
                    // id 必须在原始 JSON 里真实存在：CronTask.Id 有默认 Guid，
                    // 若靠它"自愈"，同一条坏记录每次重启都会换一个身份，运行记录再也对不上。
                    if (!element.TryGetProperty("id", out var idElement)
                        || idElement.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(idElement.GetString()))
                    {
                        corrupted++;
                        _logger.Warning("Skipped a cron task record without a stable id");
                        continue;
                    }

                    var task = element.Deserialize<CronTask>(SerializerOptions);
                    if (task == null || string.IsNullOrWhiteSpace(task.Id) || string.IsNullOrWhiteSpace(task.CronExpression))
                    {
                        corrupted++;
                        _logger.Warning("Skipped a cron task record without an id or cron expression");
                        continue;
                    }

                    if (!seenIds.Add(task.Id))
                    {
                        corrupted++;
                        _logger.Warning("Skipped duplicate cron task id {TaskId}", task.Id);
                        continue;
                    }

                    task.RecentRuns ??= new List<CronTaskRunRecord>();
                    task.RecentRuns.RemoveAll(run => run == null);
                    task.TrimRuns();
                    tasks.Add(task);
                }
                catch (Exception ex)
                {
                    corrupted++;
                    _logger.Warning(ex, "Skipped a corrupted cron task record");
                }
            }

            if (corrupted > 0)
            {
                _logger.Warning("Loaded {Count} cron task(s); isolated {Corrupted} corrupted record(s)", tasks.Count, corrupted);
            }
            else
            {
                _logger.Information("Loaded {Count} cron task(s) from {Path}", tasks.Count, _filePath);
            }

            return new CronTaskLoadResult { Tasks = tasks, CorruptedCount = corrupted };
        }
    }

    public async Task SaveAsync(IReadOnlyList<CronTask> tasks)
    {
        await _writeGate.WaitAsync();
        try
        {
            var document = new CronTaskStoreDocument
            {
                SchemaVersion = CurrentSchemaVersion,
                Tasks = tasks.Select(NormalizeForPersistence).ToList()
            };

            var json = JsonSerializer.Serialize(document, SerializerOptions);
            var temporaryPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, json);
            File.Move(temporaryPath, _filePath, overwrite: true);
            _logger.Debug("Persisted {Count} cron task(s)", document.Tasks.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to persist cron tasks to {Path}", _filePath);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public bool DeleteLegacyStoreIfPresent()
    {
        try
        {
            if (!File.Exists(_legacyFilePath)) return false;
            File.Delete(_legacyFilePath);
            _logger.Information(
                "Deleted the obsolete recurrence-based task store at {Path}; cron tasks live in {NewPath} and legacy data is never migrated",
                _legacyFilePath, _filePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to delete the obsolete task store at {Path}", _legacyFilePath);
            return false;
        }
    }

    /// <summary>持久化时把所有时刻统一到 UTC，避免同一时刻因机器时区不同而写出不同文本。</summary>
    private static CronTask NormalizeForPersistence(CronTask task)
    {
        var clone = task.Clone();
        clone.CreatedAt = clone.CreatedAt.ToUniversalTime();
        clone.UpdatedAt = clone.UpdatedAt.ToUniversalTime();
        clone.NextOccurrence = clone.NextOccurrence?.ToUniversalTime();
        foreach (var run in clone.RecentRuns)
        {
            run.ScheduledFor = run.ScheduledFor?.ToUniversalTime();
            run.StartedAt = run.StartedAt?.ToUniversalTime();
            run.CompletedAt = run.CompletedAt?.ToUniversalTime();
        }
        return clone;
    }

    private void QuarantineFile()
    {
        try
        {
            var quarantinePath = _filePath + ".corrupt";
            File.Move(_filePath, quarantinePath, overwrite: true);
            _logger.Warning("Moved the unreadable cron task store to {Path}", quarantinePath);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to quarantine the unreadable cron task store");
        }
    }

    public void Dispose() => _writeGate.Dispose();

    private sealed class CronTaskStoreDocument
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public List<CronTask> Tasks { get; set; } = new();
    }
}
