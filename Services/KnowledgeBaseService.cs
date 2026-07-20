using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>
/// 知识库服务实现
/// 使用本地文件系统存储 Markdown 格式的知识
/// 支持向量语义检索（当 Embedding 服务可用时）
/// 向量持久化到 SQLite，支持增量更新
/// </summary>
public class KnowledgeBaseService : IKnowledgeBaseService
{
    private readonly string _knowledgeBasePath;
    private readonly ILogger _logger;
    private readonly IEmbeddingService? _embeddingService;
    private readonly VectorStoreService _vectorStoreService;
    private FileSystemWatcher? _watcher;
    private readonly ConcurrentDictionary<string, DateTime> _pendingUpdates = new();
    private Timer? _debounceTimer;

    /// <summary>
    /// 文档向量缓存（内存中）
    /// </summary>
    private readonly List<DocumentVector> _vectorCache = new();

    /// <summary>
    /// 向量缓存是否已初始化
    /// </summary>
    private bool _vectorCacheInitialized;

    /// <summary>
    /// 初始化锁
    /// </summary>
    private readonly SemaphoreSlim _initLock = new(1, 1);

    #region 并发控制与安全常量

    /// <summary>
    /// 文件级别的锁（每个文件一个锁，避免全局锁影响性能）
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();

    /// <summary>
    /// 全局操作锁（用于目录操作等）
    /// </summary>
    private readonly SemaphoreSlim _globalLock = new(1, 1);

    /// <summary>
    /// 最大文件大小（10MB）
    /// </summary>
    private const int MaxFileSize = 10 * 1024 * 1024;

    /// <summary>
    /// 最大文件路径长度
    /// </summary>
    private const int MaxFilePathLength = 260;

    /// <summary>
    /// 危险路径字符
    /// </summary>
    private static readonly string[] DangerousPathPatterns = { "..", "~", "\0", "::" };

    /// <summary>
    /// 文本分块目标大小（字符）。块内语义更完整，降低字面相近但语义无关的误召回。
    /// </summary>
    private const int ChunkSize = 800;

    /// <summary>
    /// 相邻分块的重叠字符数，避免跨块边界切断语义。
    /// </summary>
    private const int ChunkOverlap = 120;

    /// <summary>
    /// 余弦相似度门控阈值：低于此值视为不相关，直接丢弃。这是“拒绝无关内容”的唯一开关。
    /// 经验值针对 text-embedding-3-small；可随模型校准（日志会打印命中的最高相似度）。
    /// </summary>
    private const double MinSimilarity = 0.30;

    /// <summary>
    /// RRF（Reciprocal Rank Fusion）平滑常数，标准取 60。基于排名融合，规避两路分数量纲不一致。
    /// </summary>
    private const int RrfK = 60;

    /// <summary>
    /// 每路检索（稠密/稀疏）的候选数量，为融合提供足够召回池。
    /// </summary>
    private const int CandidatePerSource = 40;

    #endregion

    public string KnowledgeBasePath => _knowledgeBasePath;

    public KnowledgeBaseService(ILogger logger, IEmbeddingService? embeddingService = null, IPlatformPathService? platformPathService = null)
    {
        _logger = logger.ForContext<KnowledgeBaseService>();
        _embeddingService = embeddingService;
        _vectorStoreService = new VectorStoreService(logger, platformPathService);

        // 初始化知识库目录
        if (platformPathService != null)
        {
            _knowledgeBasePath = platformPathService.GetKnowledgeBaseDirectory();
        }
        else
        {
            // 兜底逻辑
            _knowledgeBasePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Athena",
                "KnowledgeBase"
            );
        }

        Directory.CreateDirectory(_knowledgeBasePath);
        EnsureAthenaSoulExists();
        SetupWatcher();

        _logger.Information("Knowledge base service initialized at {Path}, vector search: {Enabled}",
            _knowledgeBasePath, _embeddingService?.IsConfigured ?? false);
    }

    /// <summary>
    /// 首次启动时从嵌入式资源释放 AthenaSoul.md 到知识库目录
    /// </summary>
    private void EnsureAthenaSoulExists()
    {
        var targetPath = Path.Combine(_knowledgeBasePath, "AthenaSoul.md");
        if (File.Exists(targetPath))
            return;

        try
        {
            var assembly = typeof(KnowledgeBaseService).Assembly;
            using var stream = assembly.GetManifestResourceStream("AthenaSoul.md");
            if (stream == null)
            {
                _logger.Warning("Embedded AthenaSoul.md resource not found");
                return;
            }

            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();
            File.WriteAllText(targetPath, content, Encoding.UTF8);
            _logger.Information("AthenaSoul.md extracted to {Path}", targetPath);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to extract AthenaSoul.md");
        }
    }

    private void SetupWatcher()
    {
        try
        {
            _watcher = new FileSystemWatcher(_knowledgeBasePath, "*.md")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };

            _watcher.Created += (s, e) => EnqueueUpdate(e.FullPath);
            _watcher.Changed += (s, e) => EnqueueUpdate(e.FullPath);
            _watcher.Deleted += (s, e) => EnqueueUpdate(e.FullPath);
            _watcher.Renamed += (s, e) => { EnqueueUpdate(e.OldFullPath); EnqueueUpdate(e.FullPath); };

            _watcher.EnableRaisingEvents = true;
            
            _debounceTimer = new Timer(ProcessPendingUpdates, null, Timeout.Infinite, Timeout.Infinite);
            _logger.Information("知识库文件监控已启动");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "初始化知识库监控失败");
        }
    }

    private void EnqueueUpdate(string fullPath)
    {
        var relativePath = GetRelativePath(fullPath);
        _pendingUpdates[relativePath] = DateTime.Now;
        _debounceTimer?.Change(1000, Timeout.Infinite); // 1秒防抖
    }

    private async void ProcessPendingUpdates(object? state)
    {
        var filesToUpdate = _pendingUpdates.Keys.ToList();
        foreach (var relativePath in filesToUpdate)
        {
            if (_pendingUpdates.TryRemove(relativePath, out _))
            {
                _logger.Information("检测到外部文件变更，正在后台更新索引: {File}", relativePath);
                await UpdateFileIndexAsync(relativePath);
            }
        }
    }

    /// <summary>
    /// 异步初始化（在首次使用时调用）
    /// </summary>
    public async Task InitializeAsync()
    {
        // 本地分块与 FTS 索引始终初始化；Embedding 只是可选的语义增强层。
        await LoadOrRefreshVectorsAsync();
    }

    public async Task CreateFileAsync(string relativePath, string content, string[]? tags = null)
    {
        var fullPath = GetFullPathSecure(relativePath);
        var fileLock = GetFileLock(relativePath);

        await fileLock.WaitAsync();
        try
        {
            // 内容验证
            ValidateContent(content);

            // 确保目录存在
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 如果有标签，添加到内容开头
            var finalContent = content;
            if (tags != null && tags.Length > 0)
            {
                var tagLine = $"<!-- Tags: {string.Join(", ", tags)} -->\n";
                finalContent = tagLine + content;
            }

            await File.WriteAllTextAsync(fullPath, finalContent);

            // 同步更新本地 FTS 和可选向量，保证立刻可被搜索
            try
            {
                await UpdateFileIndexAsync(relativePath);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "更新文件向量失败");
            }

            _logger.Information("创建知识文件: {Path}", relativePath);
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task AppendToFileAsync(string relativePath, string content)
    {
        var fullPath = GetFullPathSecure(relativePath);
        var fileLock = GetFileLock(relativePath);

        await fileLock.WaitAsync();
        try
        {
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"文件不存在: {relativePath}");
            }

            // 内容验证
            ValidateContent(content);

            var existingContent = await File.ReadAllTextAsync(fullPath);
            var newContent = existingContent.TrimEnd() + "\n\n" + content;

            // 大小限制检查
            if (newContent.Length > MaxFileSize)
            {
                throw new InvalidOperationException($"文件大小超过限制 ({MaxFileSize / 1024 / 1024}MB)");
            }

            await File.WriteAllTextAsync(fullPath, newContent);

            // 同步更新本地 FTS 和可选向量，保证立刻可被搜索
            try
            {
                await UpdateFileIndexAsync(relativePath);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "更新文件向量失败");
            }

            _logger.Information("追加内容到知识文件: {Path}", relativePath);
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task ReplaceFileAsync(string relativePath, string content)
    {
        var fullPath = GetFullPathSecure(relativePath);
        var fileLock = GetFileLock(relativePath);

        await fileLock.WaitAsync();
        try
        {
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"文件不存在: {relativePath}");
            }

            // 内容验证
            ValidateContent(content);

            await File.WriteAllTextAsync(fullPath, content);

            // 同步更新本地 FTS 和可选向量，保证立刻可被搜索
            try
            {
                await UpdateFileIndexAsync(relativePath);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "更新文件向量失败");
            }

            _logger.Information("替换知识文件内容: {Path}", relativePath);
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task<string?> ReadFileAsync(string relativePath)
    {
        var fullPath = GetFullPathSecure(relativePath);
        var fileLock = GetFileLock(relativePath);

        await fileLock.WaitAsync();
        try
        {
            if (!File.Exists(fullPath))
            {
                return null;
            }

            var content = await File.ReadAllTextAsync(fullPath);
            return content;
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task DeleteFileAsync(string relativePath)
    {
        var fullPath = GetFullPathSecure(relativePath);
        var fileLock = GetFileLock(relativePath);

        await fileLock.WaitAsync();
        try
        {
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"文件不存在: {relativePath}");
            }

            File.Delete(fullPath);

            // 异步删除向量
            _ = Task.Run(async () =>
            {
                try
                {
                    await UpdateFileIndexAsync(relativePath);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "删除文件索引失败");
                }
            });

            _logger.Information("删除知识文件: {Path}", relativePath);
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task DeleteDirectoryAsync(string relativePath)
    {
        var fullPath = GetFullPathSecure(relativePath);

        await _globalLock.WaitAsync();
        try
        {
            if (!Directory.Exists(fullPath))
            {
                throw new DirectoryNotFoundException($"目录不存在: {relativePath}");
            }

            // 获取目录下所有文件，用于后续清理向量
            var filesToDelete = Directory.GetFiles(fullPath, "*.md", SearchOption.AllDirectories)
                .Select(GetRelativePath)
                .ToList();

            // 递归删除目录及其所有内容
            Directory.Delete(fullPath, recursive: true);

            // 异步删除向量
            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var file in filesToDelete)
                    {
                        await UpdateFileIndexAsync(file);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "删除目录向量失败");
                }
            });

            _logger.Information("删除知识库目录: {Path}", relativePath);
        }
        finally
        {
            _globalLock.Release();
        }
    }

    /// <summary>
    /// 混合检索：稠密（余弦）+ 稀疏（FTS/BM25）双路召回，RRF 融合排序，再用余弦门控过滤无关内容。
    /// 排序与“判定相关”职责分离：RRF 负责排序（无量纲），余弦负责门控（可解释）。
    /// </summary>
    public async Task<List<KnowledgeSearchResult>> SearchAsync(string query, int maxResults = 5)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<KnowledgeSearchResult>();

        try
        {
            if (!_vectorCacheInitialized) await LoadOrRefreshVectorsAsync();

            var embeddingsOn = _embeddingService != null && _embeddingService.IsConfigured;

            // 查询向量：稠密召回 + 余弦门控共用
            float[]? queryEmbedding = null;
            if (embeddingsOn)
            {
                queryEmbedding = await _embeddingService!.GenerateEmbeddingAsync(query);
            }

            // 1) 稠密召回（余弦）。同时记录全量相似度，供门控复用（含稀疏-only 候选）
            var simByKey = new Dictionary<(string, int), double>();
            var denseDocs = new List<DocumentVector>();
            if (queryEmbedding != null && _vectorCache.Count > 0)
            {
                var scored = _vectorCache
                    .Where(d => d.Embedding != null)
                    .Select(d => (Doc: d, Sim: (double)_embeddingService!.CosineSimilarity(queryEmbedding, d.Embedding!)))
                    .ToList();
                foreach (var s in scored) simByKey[(s.Doc.FilePath, s.Doc.ChunkIndex)] = s.Sim;
                denseDocs = scored.OrderByDescending(x => x.Sim).Take(CandidatePerSource).Select(x => x.Doc).ToList();
            }

            // 2) 稀疏召回（FTS / BM25）
            var sparseHits = await _vectorStoreService.SearchFtsAsync(query, CandidatePerSource);

            // 3) RRF 融合：score(d) = Σ 1/(k + rank)，按排名合并两路，规避分数量纲不一致
            var rrf = new Dictionary<(string, int), double>();
            var snippet = new Dictionary<(string, int), (string FilePath, string Text, string Heading)>();
            var indexedByKey = _vectorCache.ToDictionary(
                document => (document.FilePath, document.ChunkIndex),
                document => document);

            for (int i = 0; i < denseDocs.Count; i++)
            {
                var key = (denseDocs[i].FilePath, denseDocs[i].ChunkIndex);
                rrf[key] = rrf.GetValueOrDefault(key) + 1.0 / (RrfK + i + 1);
                snippet[key] = (denseDocs[i].FilePath, denseDocs[i].ChunkText, denseDocs[i].HeadingPath);
            }
            for (int i = 0; i < sparseHits.Count; i++)
            {
                var key = (sparseHits[i].FilePath, sparseHits[i].ChunkIndex);
                rrf[key] = rrf.GetValueOrDefault(key) + 1.0 / (RrfK + i + 1);
                if (!snippet.ContainsKey(key))
                {
                    var heading = indexedByKey.TryGetValue(key, out var indexed)
                        ? indexed.HeadingPath
                        : string.Empty;
                    snippet[key] = (sparseHits[i].FilePath, sparseHits[i].Content, heading);
                }
            }

            if (rrf.Count == 0) return new List<KnowledgeSearchResult>();

            // 4) 余弦门控 + 按 RRF 排序。仅在有查询向量时门控（否则退化为纯词面检索）
            var candidates = rrf.Keys
                .Select(key => (
                    Rrf: rrf[key],
                    Sim: simByKey.TryGetValue(key, out var s) ? s : double.NaN,
                    Info: snippet[key]))
                .ToList();

            IEnumerable<(double Rrf, double Sim, (string FilePath, string Text, string Heading) Info)> survivors = candidates;
            if (queryEmbedding != null)
            {
                survivors = candidates.Where(x => !double.IsNaN(x.Sim) && x.Sim >= MinSimilarity);
                var topSim = candidates.Where(x => !double.IsNaN(x.Sim)).Select(x => x.Sim).DefaultIfEmpty(0).Max();
                _logger.Debug("检索 '{Query}': 候选 {Cand}，门控阈值 {Tau}，最高相似度 {Sim:F3}",
                    query, candidates.Count, MinSimilarity, topSim);
            }

            // 5) 文件级聚合：同一文件的多个命中分块合并为一条（保留最佳分块），并记录命中数。
            //    避免同文件多分块挤占 TopK，让模型感知“该主题已归属某文件”，从而倾向修改而非新建。
            var ranked = survivors.OrderByDescending(x => x.Rrf).ToList();
            var byFile = new Dictionary<string, (double Rrf, double Sim, string Text, string Heading, int Count)>();
            var fileOrder = new List<string>();
            foreach (var x in ranked)
            {
                var fp = x.Info.FilePath;
                if (byFile.TryGetValue(fp, out var agg))
                {
                    // 首次出现即最佳分块（已按 Rrf 降序），后续仅累加命中数
                    byFile[fp] = (agg.Rrf, agg.Sim, agg.Text, agg.Heading, agg.Count + 1);
                }
                else
                {
                    byFile[fp] = (x.Rrf, x.Sim, x.Info.Text, x.Info.Heading, 1);
                    fileOrder.Add(fp);
                }
            }

            return fileOrder
                .Take(maxResults)
                .Select(fp =>
                {
                    var a = byFile[fp];
                    return new KnowledgeSearchResult
                    {
                        FilePath = fp,
                        Snippet = a.Text,
                        HeadingPath = a.Heading,
                        MatchCount = a.Count,
                        RetrievalMode = queryEmbedding != null ? "hybrid" : "keyword",
                        // 展示可解释的余弦相似度；纯词面模式下退回 RRF 分
                        RelevanceScore = queryEmbedding != null ? Math.Round(a.Sim, 4) : Math.Round(a.Rrf, 4)
                    };
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "混合检索失败");
            return new List<KnowledgeSearchResult>();
        }
    }

    /// <inheritdoc />
    public async Task<List<SimilarKnowledgeFile>> FindSimilarFilesAsync(string content, double minSimilarity, int maxResults = 3)
    {
        if (string.IsNullOrWhiteSpace(content)) return new List<SimilarKnowledgeFile>();
        if (_embeddingService == null || !_embeddingService.IsConfigured) return new List<SimilarKnowledgeFile>();

        try
        {
            if (!_vectorCacheInitialized) await LoadOrRefreshVectorsAsync();
            if (_vectorCache.Count == 0) return new List<SimilarKnowledgeFile>();

            // 内容可能很长；截断到与分块尺度相当的长度即可代表主题，避免超出嵌入上限。
            var probeText = content.Length > ChunkSize * 4 ? content[..(ChunkSize * 4)] : content;
            var contentEmbedding = await _embeddingService.GenerateEmbeddingAsync(probeText);
            if (contentEmbedding == null) return new List<SimilarKnowledgeFile>();

            // 每个文件取其所有分块与目标内容的最大相似度作为该文件的相似度。
            var perFile = new Dictionary<string, (double Sim, string Text)>();
            foreach (var doc in _vectorCache)
            {
                if (doc.Embedding == null) continue;
                var sim = (double)_embeddingService.CosineSimilarity(contentEmbedding, doc.Embedding);
                if (!perFile.TryGetValue(doc.FilePath, out var cur) || sim > cur.Sim)
                {
                    perFile[doc.FilePath] = (sim, doc.ChunkText);
                }
            }

            return perFile
                .Where(kv => kv.Value.Sim >= minSimilarity)
                .OrderByDescending(kv => kv.Value.Sim)
                .Take(maxResults)
                .Select(kv => new SimilarKnowledgeFile
                {
                    FilePath = kv.Key,
                    Similarity = Math.Round(kv.Value.Sim, 4),
                    Snippet = kv.Value.Text.Length > 200 ? kv.Value.Text[..200] + "…" : kv.Value.Text
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "查重检索失败");
            return new List<SimilarKnowledgeFile>();
        }
    }

    /// <inheritdoc />
    public async Task<List<DuplicateFileCluster>> DetectDuplicateClustersAsync(double minSimilarity)
    {
        if (_embeddingService == null || !_embeddingService.IsConfigured) return new List<DuplicateFileCluster>();

        try
        {
            if (!_vectorCacheInitialized) await LoadOrRefreshVectorsAsync();
            if (_vectorCache.Count == 0) return new List<DuplicateFileCluster>();

            // 每个文件求分块向量质心（归一化留给 CosineSimilarity 处理）。
            var centroids = new Dictionary<string, float[]>();
            foreach (var group in _vectorCache.Where(d => d.Embedding != null).GroupBy(d => d.FilePath))
            {
                var docs = group.ToList();
                var dim = docs[0].Embedding!.Length;
                var acc = new float[dim];
                foreach (var d in docs)
                {
                    var emb = d.Embedding!;
                    for (int i = 0; i < dim && i < emb.Length; i++) acc[i] += emb[i];
                }
                for (int i = 0; i < dim; i++) acc[i] /= docs.Count;
                centroids[group.Key] = acc;
            }

            var files = centroids.Keys.ToList();
            if (files.Count < 2) return new List<DuplicateFileCluster>();

            // 并查集：把两两相似度 >= 阈值的文件并入同一组。
            var parent = files.ToDictionary(f => f, f => f);
            string Find(string x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(string a, string b) { var ra = Find(a); var rb = Find(b); if (ra != rb) parent[ra] = rb; }

            var pairMin = new Dictionary<string, double>(); // 组根 -> 组内最低相似度
            for (int i = 0; i < files.Count; i++)
            {
                for (int j = i + 1; j < files.Count; j++)
                {
                    var sim = (double)_embeddingService.CosineSimilarity(centroids[files[i]], centroids[files[j]]);
                    if (sim >= minSimilarity)
                    {
                        Union(files[i], files[j]);
                    }
                }
            }

            // 收集每组成员，并计算组内两两最低相似度（作为“重复强度”下界）。
            var groups = files.GroupBy(Find).Where(g => g.Count() >= 2).ToList();
            var clusters = new List<DuplicateFileCluster>();
            foreach (var g in groups)
            {
                var members = g.ToList();
                double minSim = double.MaxValue;
                for (int i = 0; i < members.Count; i++)
                    for (int j = i + 1; j < members.Count; j++)
                        minSim = Math.Min(minSim, (double)_embeddingService.CosineSimilarity(centroids[members[i]], centroids[members[j]]));

                clusters.Add(new DuplicateFileCluster
                {
                    FilePaths = members.OrderBy(m => m).ToList(),
                    MinSimilarity = Math.Round(minSim, 4)
                });
            }

            return clusters.OrderByDescending(c => c.MinSimilarity).ToList();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "重复文件聚类失败");
            return new List<DuplicateFileCluster>();
        }
    }

    /// <summary>
    /// 加载或增量刷新知识库索引。本地分块与 FTS 始终构建，向量层按配置选建。
    /// </summary>
    public async Task LoadOrRefreshVectorsAsync()
    {
        await _initLock.WaitAsync();
        try
        {
            if (_vectorCacheInitialized) return;

            await _vectorStoreService.InitializeAsync();
            var indexedDocuments = await _vectorStoreService.LoadAllVectorsAsync();
            var textStatuses = await _vectorStoreService.GetFileStatusesAsync();
            var vectorStatuses = await _vectorStoreService.GetVectorFileStatusesAsync();
            var embeddingsOn = _embeddingService != null && _embeddingService.IsConfigured;

            // Embedding 模型变化只使向量失效，不应影响本地 FTS 可用性。
            if (embeddingsOn)
            {
                var probe = await _embeddingService!.GenerateEmbeddingAsync("test");
                var currentModel = _embeddingService.ModelId ?? string.Empty;
                var currentDim = probe?.Length ?? 0;
                var stored = await _vectorStoreService.GetEmbeddingFingerprintAsync();
                var mismatch = currentDim > 0 &&
                    (stored == null || stored.Value.Model != currentModel || stored.Value.Dimension != currentDim);

                if (mismatch)
                {
                    await _vectorStoreService.ClearEmbeddingsAsync();
                    foreach (var document in indexedDocuments) document.Embedding = null;
                    vectorStatuses.Clear();
                    _logger.Information(
                        "Embedding 指纹变化（{OldModel}/{OldDim} -> {NewModel}/{NewDim}），保留 FTS 并重建向量",
                        stored?.Model ?? "(none)", stored?.Dimension ?? 0, currentModel, currentDim);
                }

                if (currentDim > 0)
                    await _vectorStoreService.SetEmbeddingFingerprintAsync(currentModel, currentDim);
            }

            var currentFiles = new Dictionary<string, (string Content, string Hash)>();
            foreach (var fullPath in Directory.GetFiles(_knowledgeBasePath, "*.md", SearchOption.AllDirectories))
            {
                var relativePath = GetRelativePath(fullPath);
                var content = await File.ReadAllTextAsync(fullPath);
                currentFiles[relativePath] = (content, VectorStoreService.ComputeFileHash(content));
            }

            foreach (var deletedPath in textStatuses.Keys.Union(vectorStatuses.Keys)
                         .Where(path => !currentFiles.ContainsKey(path)).Distinct())
            {
                await _vectorStoreService.DeleteFileIndexAsync(deletedPath);
                indexedDocuments.RemoveAll(document => document.FilePath == deletedPath);
            }

            var chunksByFile = new Dictionary<string, List<Chunk>>();
            foreach (var (filePath, file) in currentFiles)
            {
                if (textStatuses.TryGetValue(filePath, out var status) && status.FileHash == file.Hash)
                    continue;

                var chunks = SplitIntoChunks(CleanForIndex(file.Content));
                chunksByFile[filePath] = chunks;
                await SaveTextIndexAsync(filePath, file.Hash, chunks);

                indexedDocuments.RemoveAll(document => document.FilePath == filePath);
                indexedDocuments.AddRange(CreateDocuments(filePath, file.Hash, chunks));
                vectorStatuses.Remove(filePath);
            }

            if (embeddingsOn)
            {
                foreach (var (filePath, file) in currentFiles)
                {
                    if (vectorStatuses.TryGetValue(filePath, out var status) && status.FileHash == file.Hash)
                        continue;

                    var chunks = chunksByFile.TryGetValue(filePath, out var changedChunks)
                        ? changedChunks
                        : SplitIntoChunks(CleanForIndex(file.Content));

                    indexedDocuments.RemoveAll(document => document.FilePath == filePath);
                    indexedDocuments.AddRange(await GenerateAndSaveEmbeddingsAsync(filePath, file.Hash, chunks));
                }
            }

            _vectorCache.Clear();
            _vectorCache.AddRange(indexedDocuments);

            _vectorCacheInitialized = true;
            var stats = await _vectorStoreService.GetStatisticsAsync();
            _logger.Information("知识库索引初始化完成：{FileCount} 个文件，{VectorCount} 个向量，Embedding={Enabled}",
                stats.FileCount, stats.VectorCount, embeddingsOn);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载知识库索引失败");
            _vectorCacheInitialized = false;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// 使知识库索引缓存失效；下次初始化或检索时增量刷新。
    /// </summary>
    public async Task RefreshVectorCacheAsync()
    {
        // 重置初始化标志，下次搜索时会触发增量更新
        _vectorCacheInitialized = false;
    }

    /// <summary>清空派生向量数据并立即重建；本地 FTS 始终保留。</summary>
    public async Task RebuildVectorIndexAsync()
    {
        await _initLock.WaitAsync();
        try
        {
            await _vectorStoreService.InitializeAsync();
            await _vectorStoreService.ClearEmbeddingsAsync();
            _vectorCache.Clear();
            _vectorCacheInitialized = false;
            _logger.Information("已清空向量层并保留本地 FTS，开始使用当前 Embedding 配置重建向量");
        }
        finally
        {
            _initLock.Release();
        }

        await LoadOrRefreshVectorsAsync();
    }

    /// <summary>
    /// 增量更新本地 FTS，并在可用时同步更新向量。
    /// </summary>
    private async Task UpdateFileIndexAsync(string relativePath)
    {
        try
        {
            await RefreshVectorCacheAsync();
            await LoadOrRefreshVectorsAsync();
            _logger.Debug("已更新本地 FTS 及可选向量索引: {FilePath}", relativePath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "增量更新知识库索引失败: {FilePath}", relativePath);
        }
    }

    /// <summary>
    /// 一个分块：标题路径（面包屑）+ 用于展示/存储的原文。
    /// 嵌入文本 = 标题路径 + 原文（见 <see cref="BuildEmbedText"/>），让短块也带上上下文。
    /// </summary>
    private readonly record struct Chunk(string HeadingPath, string DisplayText);

    /// <summary>匹配 HTML 注释（含 &lt;!-- Tags: ... --&gt;），索引前剥离以免污染向量。</summary>
    private static readonly Regex HtmlCommentRegex = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>索引前清洗：去除 HTML 注释等噪声。</summary>
    private static string CleanForIndex(string markdown) => HtmlCommentRegex.Replace(markdown, string.Empty);

    /// <summary>构造嵌入文本：标题路径前缀 + 原文，给短块补充上下文以提升相关性。</summary>
    private static string BuildEmbedText(in Chunk c) =>
        string.IsNullOrEmpty(c.HeadingPath) ? c.DisplayText : c.HeadingPath + "\n" + c.DisplayText;

    /// <summary>
    /// 将文本分块写入本地 chunk 存储和 FTS；不依赖 Embedding。
    /// </summary>
    private async Task SaveTextIndexAsync(string relativePath, string hash, List<Chunk> chunks)
    {
        await _vectorStoreService.SaveChunksAsync(
            relativePath,
            hash,
            chunks.Select((chunk, index) => (index, chunk.DisplayText, chunk.HeadingPath)).ToList());
    }

    private static List<DocumentVector> CreateDocuments(string relativePath, string hash, List<Chunk> chunks) =>
        chunks.Select((chunk, index) => new DocumentVector
        {
            FilePath = relativePath,
            ChunkIndex = index,
            ChunkText = chunk.DisplayText,
            HeadingPath = chunk.HeadingPath,
            FileHash = hash
        }).ToList();

    private async Task<List<DocumentVector>> GenerateAndSaveEmbeddingsAsync(
        string relativePath, string hash, List<Chunk> chunks)
    {
        var documents = CreateDocuments(relativePath, hash, chunks);
        if (_embeddingService == null || !_embeddingService.IsConfigured || chunks.Count == 0)
            return documents;

        var generated = await _embeddingService.GenerateEmbeddingsAsync(chunks.Select(c => BuildEmbedText(c)));
        var embeddings = new List<(int Index, float[] Embedding)>();
        for (var i = 0; i < chunks.Count && i < generated.Count; i++)
        {
            if (generated[i] == null) continue;
            documents[i].Embedding = generated[i];
            embeddings.Add((i, generated[i]!));
        }

        await _vectorStoreService.SaveEmbeddingsAsync(relativePath, hash, embeddings);
        return documents;
    }

    /// <summary>
    /// 结构感知的 Markdown 分块：按标题（H1–H3）分节并记录标题路径；节内按目标大小打包，
    /// 相邻块保留重叠以免切断语义；代码块保持完整。
    /// </summary>
    private List<Chunk> SplitIntoChunks(string text)
    {
        var chunks = new List<Chunk>();
        if (string.IsNullOrWhiteSpace(text)) return chunks;

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var headings = new string?[3]; // H1 / H2 / H3
        var buffer = new StringBuilder();
        bool inCode = false;

        string CurrentPath() => string.Join(" > ", headings.Where(h => !string.IsNullOrEmpty(h)));

        void Flush()
        {
            var body = buffer.ToString().Trim();
            if (body.Length > 0) chunks.Add(new Chunk(CurrentPath(), body));
            buffer.Clear();
        }

        // 在同一标题节内切块后，携带上一块的尾部作为重叠，保持语义连续
        void CarryOverlap()
        {
            if (chunks.Count == 0) return;
            var last = chunks[^1].DisplayText;
            var tail = last.Length > ChunkOverlap ? last[^ChunkOverlap..] : last;
            buffer.Append(tail).Append('\n');
        }

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```")) inCode = !inCode;

            int hLevel = 0;
            if (!inCode)
            {
                if (trimmed.StartsWith("# ")) hLevel = 1;
                else if (trimmed.StartsWith("## ")) hLevel = 2;
                else if (trimmed.StartsWith("### ")) hLevel = 3;
            }

            if (hLevel > 0)
            {
                // 标题是天然边界：先收尾当前节，再更新标题栈（重叠不跨节）
                Flush();
                headings[hLevel - 1] = trimmed.TrimStart('#').Trim();
                for (int l = hLevel; l < headings.Length; l++) headings[l] = null;
                buffer.Append(line).Append('\n'); // 标题行并入下一块正文，便于展示
                continue;
            }

            bool paragraphBoundary = !inCode && string.IsNullOrWhiteSpace(trimmed);

            if (!inCode && buffer.Length > 0 && buffer.Length + line.Length > ChunkSize)
            {
                Flush();
                CarryOverlap();
            }
            else if (paragraphBoundary && buffer.Length > ChunkSize * 0.7)
            {
                Flush();
                CarryOverlap();
                continue;
            }

            buffer.Append(line).Append('\n');

            // 兜底：单段/代码块过长时强制切分
            if (!inCode && buffer.Length > ChunkSize * 1.5)
            {
                Flush();
                CarryOverlap();
            }
        }

        Flush();
        return chunks;
    }

    public Task<List<string>> ListFilesAsync()
    {
        var files = Directory.GetFiles(_knowledgeBasePath, "*.md", SearchOption.AllDirectories)
            .Select(GetRelativePath)
            .OrderBy(f => f)
            .ToList();

        return Task.FromResult(files);
    }

    public Task<List<string>> ListDirectoriesAsync()
    {
        var directories = Directory.GetDirectories(_knowledgeBasePath, "*", SearchOption.AllDirectories)
            .Select(GetRelativePath)
            .OrderBy(d => d)
            .ToList();

        return Task.FromResult(directories);
    }

    public Task<bool> FileExistsAsync(string relativePath)
    {
        try
        {
            var fullPath = GetFullPathSecure(relativePath);
            return Task.FromResult(File.Exists(fullPath));
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// 获取相对路径
    /// </summary>
    private string GetRelativePath(string fullPath)
    {
        return fullPath[_knowledgeBasePath.Length..].TrimStart(Path.DirectorySeparatorChar);
    }

    #region SEARCH/REPLACE 更新策略

    // 匹配模式常量
    private const string SearchStart = "<<<<<<< SEARCH";
    private const string Separator = "=======";
    private const string ReplaceEnd = ">>>>>>> REPLACE";

    /// <summary>
    /// 获取文件级别的锁
    /// </summary>
    private SemaphoreSlim GetFileLock(string relativePath)
    {
        return _fileLocks.GetOrAdd(relativePath, _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>
    /// 加固路径安全验证
    /// </summary>
    private string GetFullPathSecure(string relativePath)
    {
        // 空值检查
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("文件路径不能为空");

        // 长度检查
        if (relativePath.Length > MaxFilePathLength)
            throw new ArgumentException($"文件路径长度超过限制 ({MaxFilePathLength})");

        // 危险字符检查
        foreach (var pattern in DangerousPathPatterns)
        {
            if (relativePath.Contains(pattern))
                throw new ArgumentException($"路径包含非法字符: {pattern}");
        }

        // 规范化路径
        var normalized = relativePath.Replace('\\', '/').Trim('/');

        // 构建完整路径
        var fullPath = Path.Combine(_knowledgeBasePath, normalized);

        // 最终验证：确保解析后的路径仍在知识库目录内
        var fullDir = Path.GetDirectoryName(fullPath) ?? fullPath;
        var baseInfo = new DirectoryInfo(_knowledgeBasePath);

        DirectoryInfo targetInfo;
        try
        {
            targetInfo = new DirectoryInfo(fullDir);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or SecurityException)
        {
            throw new ArgumentException("无效的文件路径");
        }

        if (!targetInfo.FullName.StartsWith(baseInfo.FullName, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("路径遍历攻击检测：尝试访问知识库之外的路径");

        return fullPath;
    }

    /// <summary>
    /// 验证内容
    /// </summary>
    private void ValidateContent(string content, string paramName = "content")
    {
        if (content == null)
            throw new ArgumentNullException(paramName);

        if (content.Length > MaxFileSize)
            throw new ArgumentException($"内容大小超过限制 ({MaxFileSize / 1024 / 1024}MB)");

        // 检测二进制内容
        if (content.Contains('\0'))
            throw new ArgumentException("内容包含非法字符（可能为二进制文件）");
    }

    /// <summary>
    /// 使用 SEARCH/REPLACE 模式更新文件
    /// </summary>
    public async Task<FileUpdateResult> UpdateWithDiffAsync(
        string relativePath,
        string diffContent,
        bool fuzzyMatch = true)
    {
        var fileLock = GetFileLock(relativePath);

        await fileLock.WaitAsync();
        try
        {
            var fullPath = GetFullPathSecure(relativePath);

            if (!File.Exists(fullPath))
                return new FileUpdateResult
                {
                    Success = false,
                    Message = $"文件不存在: {relativePath}"
                };

            var fileContent = await File.ReadAllTextAsync(fullPath);

            // 解析 DIFF 内容
            var blocks = ParseDiffBlocks(diffContent);
            if (blocks.Count == 0)
                return new FileUpdateResult
                {
                    Success = false,
                    Message = "未找到有效的 SEARCH/REPLACE 块。\n" +
                              "请使用以下格式：\n" +
                              "<<<<<<< SEARCH\n要查找的原始内容\n=======\n替换后的新内容\n>>>>>>> REPLACE"
                };

            var modifiedContent = fileContent;
            var appliedCount = 0;

            foreach (var block in blocks)
            {
                var matchResult = ApplyDiffBlock(modifiedContent, block, fuzzyMatch);

                if (!matchResult.Success)
                {
                    return matchResult;
                }

                modifiedContent = matchResult.ModifiedContent!;
                appliedCount++;
            }

            // 验证并写入
            ValidateContent(modifiedContent);
            await File.WriteAllTextAsync(fullPath, modifiedContent);

            _logger.Information("SEARCH/REPLACE 更新文件成功: {Path}, 应用 {Count} 个修改块",
                relativePath, appliedCount);

            // 同步更新本地 FTS 和可选向量，保证立刻可被搜索
            try
            {
                await UpdateFileIndexAsync(relativePath);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "更新文件向量失败");
            }

            return new FileUpdateResult
            {
                Success = true,
                Message = $"成功应用 {appliedCount} 个修改",
                AppliedBlocks = appliedCount
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "SEARCH/REPLACE 更新失败: {Path}", relativePath);
            return new FileUpdateResult
            {
                Success = false,
                Message = $"更新失败: {ex.Message}"
            };
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>
    /// 解析 SEARCH/REPLACE 块
    /// </summary>
    private List<DiffBlock> ParseDiffBlocks(string diffContent)
    {
        var blocks = new List<DiffBlock>();
        var lines = diffContent.Split('\n');

        int i = 0;
        while (i < lines.Length)
        {
            // 查找 SEARCH 开始标记
            if (lines[i].TrimStart().StartsWith(SearchStart))
            {
                var block = new DiffBlock();
                i++;

                // 收集 SEARCH 内容
                var searchLines = new List<string>();
                while (i < lines.Length && !lines[i].Trim().StartsWith(Separator))
                {
                    searchLines.Add(lines[i]);
                    i++;
                }
                block.SearchContent = string.Join("\n", searchLines).Trim('\r', '\n');

                if (i < lines.Length && lines[i].Trim().StartsWith(Separator))
                    i++;

                // 收集 REPLACE 内容
                var replaceLines = new List<string>();
                while (i < lines.Length && !lines[i].TrimStart().StartsWith(ReplaceEnd))
                {
                    replaceLines.Add(lines[i]);
                    i++;
                }
                block.ReplaceContent = string.Join("\n", replaceLines).Trim('\r', '\n');

                if (i < lines.Length && lines[i].TrimStart().StartsWith(ReplaceEnd))
                    i++;

                if (!string.IsNullOrEmpty(block.SearchContent))
                {
                    blocks.Add(block);
                }
            }
            else
            {
                i++;
            }
        }

        return blocks;
    }

    /// <summary>
    /// 应用单个 DIFF 块
    /// </summary>
    private FileUpdateResult ApplyDiffBlock(string content, DiffBlock block, bool fuzzyMatch)
    {
        // 首先尝试精确匹配
        var exactIndex = content.IndexOf(block.SearchContent);

        if (exactIndex >= 0)
        {
            // 检查是否有多处匹配
            var secondMatch = content.IndexOf(block.SearchContent, exactIndex + 1);
            if (secondMatch >= 0)
            {
                var matches = FindAllMatches(content, block.SearchContent);
                return new FileUpdateResult
                {
                    Success = false,
                    Message = $"找到 {matches.Count} 处匹配，请提供更多上下文以唯一标识要修改的位置",
                    MultipleMatches = matches
                };
            }

            var newContent = content.Substring(0, exactIndex)
                           + block.ReplaceContent
                           + content.Substring(exactIndex + block.SearchContent.Length);

            return new FileUpdateResult
            {
                Success = true,
                ModifiedContent = newContent,
                LineNumber = content.Substring(0, exactIndex).Split('\n').Length
            };
        }

        // 精确匹配失败，尝试模糊匹配
        if (fuzzyMatch)
        {
            return FuzzyMatchAndReplace(content, block);
        }

        return new FileUpdateResult
        {
            Success = false,
            Message = "未找到匹配的 SEARCH 内容。\n" +
                      "请确保 SEARCH 块与文件中的原始内容完全一致。\n" +
                      "提示：可以尝试复制文件中的原始文本，而不是手动输入。"
        };
    }

    /// <summary>
    /// 模糊匹配（忽略空格、制表符差异）
    /// </summary>
    private FileUpdateResult FuzzyMatchAndReplace(string content, DiffBlock block)
    {
        var normalizedContent = NormalizeForComparison(content);
        var normalizedSearch = NormalizeForComparison(block.SearchContent);

        var index = normalizedContent.Normalized.IndexOf(normalizedSearch.Normalized);
        if (index < 0)
        {
            // 计算相似度，提供帮助信息
            var similarity = CalculateSimilarity(normalizedContent.Normalized, normalizedSearch.Normalized);
            return new FileUpdateResult
            {
                Success = false,
                Message = $"未找到匹配内容（相似度: {similarity:P1}）。\n" +
                          "请确保 SEARCH 块与文件中的原始内容尽量一致。\n" +
                          "如果内容有较大差异，请先使用 read_memory_file 查看当前文件内容。"
            };
        }

        // 映射回原始位置
        var originalStart = normalizedContent.PositionMap[index];
        var searchEndIndex = Math.Min(index + normalizedSearch.Normalized.Length - 1, normalizedContent.PositionMap.Length - 1);
        var originalEnd = normalizedContent.PositionMap[searchEndIndex];

        // 检查多处匹配
        var secondMatch = normalizedContent.Normalized.IndexOf(normalizedSearch.Normalized, index + 1);
        if (secondMatch >= 0)
        {
            return new FileUpdateResult
            {
                Success = false,
                Message = "找到多处模糊匹配，请提供更多上下文以唯一标识要修改的位置"
            };
        }

        var newContent = content.Substring(0, originalStart)
                       + block.ReplaceContent
                       + content.Substring(originalEnd + 1);

        return new FileUpdateResult
        {
            Success = true,
            ModifiedContent = newContent,
            LineNumber = content.Substring(0, originalStart).Split('\n').Length
        };
    }

    /// <summary>
    /// 标准化文本用于比较（忽略空格差异）
    /// </summary>
    private (string Normalized, int[] PositionMap) NormalizeForComparison(string text)
    {
        var normalized = new StringBuilder();
        var positionMap = new List<int>();

        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsWhiteSpace(c))
            {
                normalized.Append(char.ToLower(c));
                positionMap.Add(i);
            }
        }

        return (normalized.ToString(), positionMap.ToArray());
    }

    /// <summary>
    /// 计算文本相似度（基于 Levenshtein 距离）
    /// </summary>
    private static double CalculateSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 1.0;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;

        var distance = LevenshteinDistance(a, b);
        var maxLen = Math.Max(a.Length, b.Length);
        return 1.0 - (double)distance / maxLen;
    }

    /// <summary>
    /// 计算 Levenshtein 距离
    /// </summary>
    private static int LevenshteinDistance(string a, string b)
    {
        var matrix = new int[a.Length + 1, b.Length + 1];

        for (int i = 0; i <= a.Length; i++) matrix[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) matrix[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                matrix[i, j] = Math.Min(
                    Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                    matrix[i - 1, j - 1] + cost);
            }
        }

        return matrix[a.Length, b.Length];
    }

    /// <summary>
    /// 查找所有匹配位置
    /// </summary>
    private List<string> FindAllMatches(string content, string search)
    {
        var matches = new List<string>();
        var index = 0;

        while ((index = content.IndexOf(search, index)) != -1)
        {
            var start = Math.Max(0, index - 20);
            var end = Math.Min(content.Length, index + search.Length + 20);
            matches.Add($"...{content.Substring(start, end - start)}...");
            index++;
        }

        return matches;
    }

    /// <summary>
    /// DIFF 块数据结构
    /// </summary>
    private class DiffBlock
    {
        public string SearchContent { get; set; } = string.Empty;
        public string ReplaceContent { get; set; } = string.Empty;
    }

    #endregion
}
