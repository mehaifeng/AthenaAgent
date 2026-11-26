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
public class KnowledgeBaseService : IKnowledgeBaseService, IDisposable
{
    private readonly string _knowledgeBasePath;
    private readonly ILogger _logger;
    private readonly IEmbeddingService? _embeddingService;
    private readonly VectorStoreService _vectorStoreService;
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;

    // A refresh publishes one complete snapshot. An invalidation arriving during a
    // refresh remains pending until a later pass loads that revision.
    private sealed class IndexSnapshot(IReadOnlyList<DocumentVector> documents)
    {
        public IReadOnlyList<DocumentVector> Documents { get; } = documents;
        public IReadOnlyDictionary<(string, int), DocumentVector> ByKey { get; } =
            documents.ToDictionary(document => (document.FilePath, document.ChunkIndex));
    }
    private volatile IndexSnapshot _index = new([]);
    private long _indexRevision;
    private long _loadedIndexRevision = -1;

    private bool IndexNeedsRefresh =>
        Volatile.Read(ref _loadedIndexRevision) != Volatile.Read(ref _indexRevision);

    /// <summary>
    /// 初始化锁
    /// </summary>
    private readonly SemaphoreSlim _initLock = new(1, 1);

    #region 文件写入与检索常量

    /// <summary>
    /// 按路径串行写入新知识文件。
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();

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

            _debounceTimer = new Timer(RefreshIndexFromWatcher, null, Timeout.Infinite, Timeout.Infinite);
            _watcher.Created += (_, _) => ScheduleIndexRefresh();
            _watcher.Changed += (_, _) => ScheduleIndexRefresh();
            _watcher.Deleted += (_, _) => ScheduleIndexRefresh();
            _watcher.Renamed += (_, _) => ScheduleIndexRefresh();
            _watcher.EnableRaisingEvents = true;
            _logger.Information("Knowledge base file watcher started");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to initialize knowledge base watcher");
        }
    }

    // The refresh compares all file hashes, so one scan covers the entire event burst.
    private void ScheduleIndexRefresh() => _debounceTimer?.Change(1000, Timeout.Infinite);

    private void RefreshIndexFromWatcher(object? _)
        => AsyncEventGuard.Run(RefreshIndexAsync, nameof(RefreshIndexFromWatcher));

    /// <summary>初始化本地文本索引，以及已配置的向量层。</summary>
    public Task InitializeAsync() => LoadOrRefreshVectorsAsync();

    public async Task CreateFileAsync(string relativePath, string content, string[]? tags = null)
    {
        var fullPath = GetFullPathSecure(relativePath);
        var fileLock = _fileLocks.GetOrAdd(relativePath, static _ => new SemaphoreSlim(1, 1));

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

            // Await indexing after the file write. The Markdown remains saved if indexing fails.
            try
            {
                await RefreshIndexAsync();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to refresh knowledge base index for {Path}", relativePath);
            }

            _logger.Information("Created knowledge file: {Path}", relativePath);
        }
        finally
        {
            fileLock.Release();
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
            if (IndexNeedsRefresh) await LoadOrRefreshVectorsAsync();
            var snapshot = _index;
            var vectorCache = snapshot.Documents;

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
            if (queryEmbedding != null && vectorCache.Count > 0)
            {
                var scored = vectorCache
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
                    var heading = snapshot.ByKey.TryGetValue(key, out var indexed)
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
                _logger.Debug("Search '{Query}': candidates {Cand}, gating threshold {Tau}, top similarity {Sim:F3}",
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
            _logger.Error(ex, "Hybrid retrieval failed");
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
            if (IndexNeedsRefresh) await LoadOrRefreshVectorsAsync();
            var vectorCache = _index.Documents;
            if (vectorCache.Count == 0) return new List<SimilarKnowledgeFile>();

            // 内容可能很长；截断到与分块尺度相当的长度即可代表主题，避免超出嵌入上限。
            var probeText = content.Length > ChunkSize * 4 ? content[..(ChunkSize * 4)] : content;
            var contentEmbedding = await _embeddingService.GenerateEmbeddingAsync(probeText);
            if (contentEmbedding == null) return new List<SimilarKnowledgeFile>();

            // 每个文件取其所有分块与目标内容的最大相似度作为该文件的相似度。
            var perFile = new Dictionary<string, (double Sim, string Text)>();
            foreach (var doc in vectorCache)
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
            _logger.Error(ex, "Duplicate-check retrieval failed");
            return new List<SimilarKnowledgeFile>();
        }
    }

    /// <inheritdoc />
    public async Task<List<DuplicateFileCluster>> DetectDuplicateClustersAsync(double minSimilarity)
    {
        if (_embeddingService == null || !_embeddingService.IsConfigured) return new List<DuplicateFileCluster>();

        try
        {
            if (IndexNeedsRefresh) await LoadOrRefreshVectorsAsync();
            var vectorCache = _index.Documents;
            if (vectorCache.Count == 0) return new List<DuplicateFileCluster>();

            // 每个文件求分块向量质心（归一化留给 CosineSimilarity 处理）。
            var centroids = new Dictionary<string, float[]>();
            foreach (var group in vectorCache.Where(d => d.Embedding != null).GroupBy(d => d.FilePath))
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
            _logger.Error(ex, "Duplicate file clustering failed");
            return new List<DuplicateFileCluster>();
        }
    }

    /// <summary>
    /// 加载或增量刷新知识库索引。本地分块与 FTS 始终构建，向量层按配置选建。
    /// </summary>
    private async Task LoadOrRefreshVectorsAsync()
    {
        await _initLock.WaitAsync();
        try
        {
            var revision = Volatile.Read(ref _indexRevision);
            if (_loadedIndexRevision == revision) return;

            await _vectorStoreService.InitializeAsync();
            var indexedDocuments = await _vectorStoreService.LoadIndexedDocumentsAsync();
            var textFileHashes = await _vectorStoreService.GetTextFileHashesAsync();
            var vectorFileHashes = await _vectorStoreService.GetVectorFileHashesAsync();
            var embeddingsOn = _embeddingService != null && _embeddingService.IsConfigured;

            // Embedding 模型变化只使向量失效，不应影响本地 FTS 可用性。
            if (embeddingsOn)
            {
                var probe = await _embeddingService!.GenerateEmbeddingAsync("test");
                var currentModel = _embeddingService.ModelId ?? string.Empty;
                var currentDim = probe?.Length ?? 0;
                var stored = await _vectorStoreService.GetEmbeddingFingerprintAsync();

                if (currentDim == 0)
                {
                    embeddingsOn = false;
                    _logger.Warning(
                        "Embedding probe failed; skipping this round of vector generation and keeping FTS: {Model}",
                        currentModel);
                }

                var mismatch = currentDim > 0 &&
                    (stored == null || stored.Value.Model != currentModel || stored.Value.Dimension != currentDim);

                if (mismatch)
                {
                    await _vectorStoreService.ClearEmbeddingsAsync();
                    foreach (var document in indexedDocuments) document.Embedding = null;
                    vectorFileHashes.Clear();
                    _logger.Information(
                        "Embedding fingerprint changed ({OldModel}/{OldDim} -> {NewModel}/{NewDim}); keeping FTS and rebuilding vectors",
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

            foreach (var deletedPath in textFileHashes.Keys.Union(vectorFileHashes.Keys)
                         .Where(path => !currentFiles.ContainsKey(path)))
            {
                await _vectorStoreService.DeleteFileIndexAsync(deletedPath);
                indexedDocuments.RemoveAll(document => document.FilePath == deletedPath);
            }

            var chunksByFile = new Dictionary<string, List<Chunk>>();
            foreach (var (filePath, file) in currentFiles)
            {
                if (textFileHashes.TryGetValue(filePath, out var indexedHash) && indexedHash == file.Hash)
                    continue;

                var chunks = SplitIntoChunks(CleanForIndex(file.Content));
                chunksByFile[filePath] = chunks;
                await SaveTextIndexAsync(filePath, file.Hash, chunks);

                indexedDocuments.RemoveAll(document => document.FilePath == filePath);
                indexedDocuments.AddRange(CreateDocuments(filePath, chunks));
                vectorFileHashes.Remove(filePath);
            }

            if (embeddingsOn)
            {
                foreach (var (filePath, file) in currentFiles)
                {
                    if (vectorFileHashes.TryGetValue(filePath, out var indexedHash) && indexedHash == file.Hash)
                        continue;

                    var chunks = chunksByFile.TryGetValue(filePath, out var changedChunks)
                        ? changedChunks
                        : SplitIntoChunks(CleanForIndex(file.Content));

                    indexedDocuments.RemoveAll(document => document.FilePath == filePath);
                    indexedDocuments.AddRange(await GenerateAndSaveEmbeddingsAsync(filePath, file.Hash, chunks));
                }
            }

            var stats = await _vectorStoreService.GetStatisticsAsync();
            _logger.Information("Knowledge base index initialized: {FileCount} file(s), {VectorCount} vector(s), embedding={Enabled}",
                stats.FileCount, stats.VectorCount, embeddingsOn);
            _index = new IndexSnapshot(indexedDocuments);
            Volatile.Write(ref _loadedIndexRevision, revision);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load knowledge base index");
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// 使知识库索引缓存失效；下次初始化或检索时增量刷新。
    /// </summary>
    public Task RefreshVectorCacheAsync()
    {
        Interlocked.Increment(ref _indexRevision);
        return Task.CompletedTask;
    }

    /// <summary>清空派生向量数据并立即重建；本地 FTS 始终保留。</summary>
    public async Task<VectorIndexRebuildResult> RebuildVectorIndexAsync()
    {
        await _initLock.WaitAsync();
        try
        {
            await _vectorStoreService.InitializeAsync();
            await _vectorStoreService.ClearEmbeddingsAsync();
            _index = new IndexSnapshot([]);
            await RefreshVectorCacheAsync();
            _logger.Information("Vector layer cleared while keeping local FTS; rebuilding vectors with the current embedding configuration");
        }
        finally
        {
            _initLock.Release();
        }

        await LoadOrRefreshVectorsAsync();

        var stats = await _vectorStoreService.GetStatisticsAsync();
        var fullyIndexedFiles = await _vectorStoreService.GetVectorFileHashesAsync();
        var result = new VectorIndexRebuildResult
        {
            EmbeddingConfigured = _embeddingService?.IsConfigured ?? false,
            FileCount = stats.FileCount,
            ChunkCount = _index.Documents.Count,
            VectorCount = stats.VectorCount,
            FullyIndexedFileCount = fullyIndexedFiles.Count
        };

        if (!result.IsFullyIndexed)
        {
            _logger.Warning(
                "Vector index rebuild incomplete: EmbeddingConfigured={Configured}, Files={Files}, Chunks={Chunks}, Vectors={Vectors}, FullyIndexedFiles={FullyIndexedFiles}",
                result.EmbeddingConfigured, result.FileCount, result.ChunkCount, result.VectorCount, result.FullyIndexedFileCount);
        }

        return result;
    }

    private async Task RefreshIndexAsync()
    {
        await RefreshVectorCacheAsync();
        await LoadOrRefreshVectorsAsync();
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

    private static List<DocumentVector> CreateDocuments(string relativePath, List<Chunk> chunks) =>
        chunks.Select((chunk, index) => new DocumentVector
        {
            FilePath = relativePath,
            ChunkIndex = index,
            ChunkText = chunk.DisplayText,
            HeadingPath = chunk.HeadingPath
        }).ToList();

    private async Task<List<DocumentVector>> GenerateAndSaveEmbeddingsAsync(
        string relativePath, string hash, List<Chunk> chunks)
    {
        var documents = CreateDocuments(relativePath, chunks);
        if (_embeddingService == null || !_embeddingService.IsConfigured || chunks.Count == 0)
            return documents;

        var embedTexts = chunks.Select(chunk => BuildEmbedText(chunk)).ToList();
        var generated = await _embeddingService.GenerateEmbeddingsAsync(embedTexts);

        // 部分 OpenAI-compatible 路由会接受批量请求却返回空/不完整集合。
        // 不能按不明的返回顺序写入错误的分块，因此整批逐条重试。
        if (generated.Count != chunks.Count || generated.Any(embedding => embedding == null))
        {
            _logger.Warning(
                "Embedding batch response incomplete: {FilePath} returned {Returned}/{Expected}; will retry per item",
                relativePath, generated.Count, chunks.Count);
            generated = new List<float[]?>();
            foreach (var text in embedTexts)
            {
                generated.Add(await _embeddingService.GenerateEmbeddingAsync(text));
            }
        }

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
    private static void ValidateContent(string content)
    {
        if (content == null)
            throw new ArgumentNullException(nameof(content));

        if (content.Length > MaxFileSize)
            throw new ArgumentException($"内容大小超过限制 ({MaxFileSize / 1024 / 1024}MB)");

        // 检测二进制内容
        if (content.Contains('\0'))
            throw new ArgumentException("内容包含非法字符（可能为二进制文件）");
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
        _initLock.Dispose();
        foreach (var gate in _fileLocks.Values) gate.Dispose();
        _vectorStoreService.Dispose();
    }
}
