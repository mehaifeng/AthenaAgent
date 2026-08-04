using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>
/// 知识库定期整理服务：后台计时器按配置间隔触发一轮整理——
/// 备份快照 → 离线向量聚类找疑似重复 → 派发 headless 整理 Agent 合并去重 → 记录运行态。
/// 独立于 TaskScheduler，避免维护任务出现在用户任务列表中。
/// </summary>
public sealed class KnowledgeBaseMaintenanceService : IKnowledgeBaseMaintenanceService, IDisposable
{
    private readonly IConfigService _configService;
    private readonly IKnowledgeBaseService _knowledgeBase;
    private readonly IEmbeddingService _embeddingService;
    private readonly IPlatformPathService _pathService;
    private readonly KnowledgeBaseMaintenanceRunner _runner;
    private readonly ILogger _logger;

    private readonly string _stateFilePath;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly Timer _timer;
    private bool _isRunning;
    private bool _started;
    private bool _disposed;

    // 文件质心相似度 ≥ 此值判为疑似重复。质心是分块均值、较平滑，取值略高于写入查重门槛。
    private const double ClusterSimilarityThreshold = 0.80;
    private const int MaxBackups = 5;
    private static readonly TimeSpan CheckPeriod = TimeSpan.FromHours(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    public KnowledgeMaintenanceState State { get; private set; } = new();
    public bool IsRunning => _isRunning;
    public event EventHandler? StateChanged;

    public KnowledgeBaseMaintenanceService(
        IConfigService configService,
        IKnowledgeBaseService knowledgeBase,
        IEmbeddingService embeddingService,
        IPlatformPathService pathService,
        KnowledgeBaseMaintenanceRunner runner,
        ILogger logger)
    {
        _configService = configService;
        _knowledgeBase = knowledgeBase;
        _embeddingService = embeddingService;
        _pathService = pathService;
        _runner = runner;
        _logger = logger.ForContext<KnowledgeBaseMaintenanceService>();

        _stateFilePath = Path.Combine(_pathService.GetAppDataDirectory(), "kb_maintenance_state.json");
        LoadState();

        _timer = new Timer(OnTimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        _timer.Change(StartupDelay, CheckPeriod);
        _logger.Information("Knowledge base maintenance service started (checking every {Period})", CheckPeriod);
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _logger.Information("Knowledge base maintenance service stopped");
    }

    private void OnTimerTick(object? state)
    {
        _ = TickAsync();
    }

    private async Task TickAsync()
    {
        try
        {
            var config = _configService.Load();
            if (!config.KnowledgeMaintenanceEnabled) return;
            if (_isRunning) return;

            var intervalDays = Math.Max(1, config.KnowledgeMaintenanceIntervalDays);
            var due = State.LastRunUtc == null
                      || (DateTime.UtcNow - State.LastRunUtc.Value) >= TimeSpan.FromDays(intervalDays);
            if (!due) return;

            _logger.Information("Knowledge base maintenance due; starting automatic consolidation");
            await RunNowAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Scheduled knowledge base maintenance check failed");
        }
    }

    public async Task<KnowledgeMaintenanceState> RunNowAsync(CancellationToken cancellationToken = default)
    {
        // 单轮互斥：已有整理在跑则直接返回当前状态。
        if (!await _runLock.WaitAsync(0, cancellationToken))
        {
            return State;
        }

        _isRunning = true;
        RaiseStateChanged();

        try
        {
            if (!_embeddingService.IsConfigured)
            {
                UpdateState("Skipped", "Embedding 未配置，无法进行向量聚类去重。", 0, touchTime: false);
                return State;
            }

            var clusters = await _knowledgeBase.DetectDuplicateClustersAsync(ClusterSimilarityThreshold);
            if (clusters.Count == 0)
            {
                UpdateState("NoDuplicates", "未发现疑似重复文件，无需整理。", 0);
                _logger.Information("Knowledge base maintenance: no suspected duplicate files found");
                return State;
            }

            // 编辑/删除前先备份快照，作为误删保险。
            TryBackupKnowledgeBase();

            var instruction = BuildInstruction(clusters);
            var (success, summary) = await _runner.RunAsync(instruction, cancellationToken);

            // 兜底刷新向量（Agent 的 modify/delete 已各自触发刷新，这里确保最终一致）。
            try { await _knowledgeBase.RefreshVectorCacheAsync(); } catch { /* 忽略 */ }

            UpdateState(success ? "Succeeded" : "Failed", summary, clusters.Count);
            _logger.Information("Knowledge base maintenance completed: {Outcome}, touching {Groups} group(s). {Summary}",
                success ? "Succeeded" : "Failed", clusters.Count, summary);
            return State;
        }
        catch (OperationCanceledException)
        {
            UpdateState("Skipped", "整理被取消。", 0, touchTime: false);
            return State;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Knowledge base maintenance run failed");
            UpdateState("Failed", ex.Message, 0);
            return State;
        }
        finally
        {
            _isRunning = false;
            _runLock.Release();
            RaiseStateChanged();
        }
    }

    private static string BuildInstruction(System.Collections.Generic.IReadOnlyList<DuplicateFileCluster> clusters)
    {
        var sb = new StringBuilder();
        sb.AppendLine("A backup of the knowledge base has already been taken, so you may edit and delete memory files freely.");
        sb.AppendLine();
        sb.AppendLine($"{clusters.Count} group(s) of likely-duplicate memory files were detected by vector similarity. " +
                      "Consolidate each group into ONE canonical file, preserving every unique fact, then delete the redundant files. " +
                      "Skip any group whose files are actually about different topics.");
        sb.AppendLine();

        for (int i = 0; i < clusters.Count; i++)
        {
            var c = clusters[i];
            sb.AppendLine($"Group {i + 1} (min similarity {c.MinSimilarity:F2}):");
            foreach (var fp in c.FilePaths)
            {
                sb.AppendLine($"  - {fp}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("Proceed now. When finished, summarize which files you merged and deleted.");
        return sb.ToString();
    }

    private void TryBackupKnowledgeBase()
    {
        try
        {
            var kbRoot = _knowledgeBase.KnowledgeBasePath;
            if (!Directory.Exists(kbRoot)) return;

            var backupRoot = Path.Combine(_pathService.GetAppDataDirectory(), "KnowledgeBase.backup");
            Directory.CreateDirectory(backupRoot);

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var dest = Path.Combine(backupRoot, stamp);
            Directory.CreateDirectory(dest);

            // 仅备份 .md 知识文件（跳过 vectors.db 等），保留相对目录结构。
            var files = Directory.GetFiles(kbRoot, "*.md", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var rel = Path.GetRelativePath(kbRoot, file);
                var target = Path.Combine(dest, rel);
                var targetDir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
                File.Copy(file, target, overwrite: true);
            }

            _logger.Information("Backed up {Count} file(s) to {Dest} before maintenance", files.Length, dest);
            PruneOldBackups(backupRoot);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Knowledge base backup failed (continuing maintenance)");
        }
    }

    private void PruneOldBackups(string backupRoot)
    {
        try
        {
            var dirs = Directory.GetDirectories(backupRoot)
                .OrderByDescending(d => d)
                .Skip(MaxBackups)
                .ToList();
            foreach (var dir in dirs)
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* 忽略单个失败 */ }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to clean up old backups");
        }
    }

    private void UpdateState(string outcome, string report, int mergedGroups, bool touchTime = true)
    {
        if (touchTime) State.LastRunUtc = DateTime.UtcNow;
        State.LastOutcome = outcome;
        State.LastReport = report;
        State.LastMergedGroups = mergedGroups;
        SaveState();
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_stateFilePath)) return;
            var json = File.ReadAllText(_stateFilePath);
            var loaded = JsonSerializer.Deserialize<KnowledgeMaintenanceState>(json);
            if (loaded != null) State = loaded;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to read knowledge base maintenance status; using default status");
        }
    }

    private void SaveState()
    {
        try
        {
            var json = JsonSerializer.Serialize(State, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_stateFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to save knowledge base maintenance status");
        }
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _timer.Dispose();
        _runLock.Dispose();
    }
}
