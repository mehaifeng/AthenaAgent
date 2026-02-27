using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
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
    /// 文本分块大小（字符）
    /// </summary>
    private const int ChunkSize = 300;

    #endregion

    public string KnowledgeBasePath => _knowledgeBasePath;

    public KnowledgeBaseService(ILogger logger, IEmbeddingService? embeddingService = null, IPlatformPathService? platformPathService = null)
    {
        _logger = logger.ForContext<KnowledgeBaseService>();
        _embeddingService = embeddingService;
        _vectorStoreService = new VectorStoreService(logger);

        // 初始化知识库目录
        if (platformPathService != null)
        {
            _knowledgeBasePath = platformPathService.GetKnowledgeBaseDirectory();
        }
        else
        {
            // 兼容旧的调用方式
            _knowledgeBasePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Athena",
                "KnowledgeBase"
            );
        }

        Directory.CreateDirectory(_knowledgeBasePath);
        InitializeDefaultStructure();

        _logger.Information("知识库服务初始化完成，路径: {Path}, 向量检索: {Enabled}",
            _knowledgeBasePath, _embeddingService?.IsConfigured ?? false);
    }

    /// <summary>
    /// 异步初始化（在首次使用时调用）
    /// </summary>
    public async Task InitializeAsync()
    {
        await _vectorStoreService.InitializeAsync();

        if (_embeddingService?.IsConfigured == true)
        {
            await LoadOrRefreshVectorsAsync();
        }
    }

    /// <summary>
    /// 初始化默认目录结构
    /// </summary>
    private void InitializeDefaultStructure()
    {
        var directories = new[] { "Characters", "Memories", "Preferences", "Scenes" };

        foreach (var dir in directories)
        {
            var path = Path.Combine(_knowledgeBasePath, dir);
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                _logger.Debug("创建知识库目录: {Directory}", dir);
            }
        }

        // 创建默认的用户配置文件
        var userProfilePath = Path.Combine(_knowledgeBasePath, "Characters", "user_profile.md");
        if (!File.Exists(userProfilePath))
        {
            var defaultContent = @"# 用户资料

## 基本信息
<!-- AI 会在这里记录用户的基本信息 -->

## 偏好
<!-- AI 会在这里记录用户的偏好 -->

## 重要事件
<!-- AI 会在这里记录重要的事件 -->
";
            File.WriteAllText(userProfilePath, defaultContent);
            _logger.Debug("创建默认用户配置文件");
        }
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

            // 异步增量更新向量
            _ = Task.Run(async () =>
            {
                try
                {
                    await UpdateFileVectorsAsync(relativePath);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "更新文件向量失败");
                }
            });

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

            // 异步增量更新向量
            _ = Task.Run(async () =>
            {
                try
                {
                    await UpdateFileVectorsAsync(relativePath);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "更新文件向量失败");
                }
            });

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

            // 异步增量更新向量
            _ = Task.Run(async () =>
            {
                try
                {
                    await UpdateFileVectorsAsync(relativePath);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "更新文件向量失败");
                }
            });

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
                    await UpdateFileVectorsAsync(relativePath);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "删除文件向量失败");
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
                        await UpdateFileVectorsAsync(file);
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

    public async Task<List<KnowledgeSearchResult>> SearchAsync(string query, int maxResults = 5)
    {
        // 如果 Embedding 服务不可用，返回空结果
        if (_embeddingService == null || !_embeddingService.IsConfigured)
        {
            _logger.Warning("Embedding 服务未配置，无法搜索知识库");
            return new List<KnowledgeSearchResult>();
        }

        return await SearchWithEmbeddingAsync(query, maxResults);
    }

    /// <summary>
    /// 使用向量语义检索
    /// </summary>
    private async Task<List<KnowledgeSearchResult>> SearchWithEmbeddingAsync(string query, int maxResults)
    {
        var results = new List<KnowledgeSearchResult>();

        try
        {
            // 确保向量缓存已初始化
            if (!_vectorCacheInitialized)
            {
                await LoadOrRefreshVectorsAsync();
            }

            // 如果缓存仍为空，返回空结果
            if (_vectorCache.Count == 0)
            {
                _logger.Debug("向量缓存为空，无搜索结果");
                return results;
            }

            // 生成查询向量
            var queryEmbedding = await _embeddingService!.GenerateEmbeddingAsync(query);
            if (queryEmbedding == null)
            {
                _logger.Warning("生成查询向量失败");
                return results;
            }

            // 计算所有文档的相似度
            var scoredDocs = _vectorCache
                .Where(doc => doc.Embedding != null)
                .Select(doc => new
                {
                    Document = doc,
                    Similarity = _embeddingService.CosineSimilarity(queryEmbedding, doc.Embedding!)
                })
                .OrderByDescending(x => x.Similarity)
                .Take(maxResults * 2) // 多取一些，后续去重
                .ToList();

            // 按文件去重，保留最高分
            var uniqueResults = scoredDocs
                .GroupBy(x => x.Document.FilePath)
                .Select(g => g.OrderByDescending(x => x.Similarity).First())
                .Take(maxResults)
                .ToList();

            foreach (var item in uniqueResults)
            {
                results.Add(new KnowledgeSearchResult
                {
                    FilePath = item.Document.FilePath,
                    Snippet = item.Document.ChunkText,
                    RelevanceScore = item.Similarity
                });
            }

            _logger.Debug("向量搜索 '{Query}' 找到 {Count} 个结果", query, results.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "向量搜索失败");
        }

        return results;
    }

    /// <summary>
    /// 加载或增量刷新向量缓存
    /// 从 SQLite 加载已有向量，仅对新增/修改的文件调用 API
    /// </summary>
    public async Task LoadOrRefreshVectorsAsync()
    {
        await _initLock.WaitAsync();
        try
        {
            if (_vectorCacheInitialized) return;

            // 初始化数据库
            await _vectorStoreService.InitializeAsync();

            // 从数据库加载已有向量
            var existingVectors = await _vectorStoreService.LoadAllVectorsAsync();
            var fileStatuses = await _vectorStoreService.GetFileStatusesAsync();

            _logger.Information("从数据库加载 {VectorCount} 个向量，{FileCount} 个文件状态",
                existingVectors.Count, fileStatuses.Count);

            // 获取当前文件列表
            var currentFiles = Directory.GetFiles(_knowledgeBasePath, "*.md", SearchOption.AllDirectories)
                .Select(GetRelativePath)
                .ToHashSet();

            // 找出需要处理的文件
            var filesToProcess = new List<string>();
            var filesToDelete = new List<string>();

            foreach (var filePath in currentFiles)
            {
                var fullPath = GetFullPathSecure(filePath);
                var content = await File.ReadAllTextAsync(fullPath);
                var hash = VectorStoreService.ComputeFileHash(content);

                if (!fileStatuses.TryGetValue(filePath, out var status) || status.FileHash != hash)
                {
                    filesToProcess.Add(filePath);
                }
            }

            // 找出已删除的文件
            foreach (var filePath in fileStatuses.Keys)
            {
                if (!currentFiles.Contains(filePath))
                {
                    filesToDelete.Add(filePath);
                }
            }

            // 删除已删除文件的向量
            foreach (var filePath in filesToDelete)
            {
                await _vectorStoreService.DeleteFileVectorsAsync(filePath);
                _logger.Debug("删除已删除文件的向量: {FilePath}", filePath);
            }

            // 过滤掉已删除文件的向量
            var validVectors = existingVectors
                .Where(v => currentFiles.Contains(v.FilePath) && !filesToProcess.Contains(v.FilePath))
                .ToList();

            _vectorCache.Clear();
            _vectorCache.AddRange(validVectors);

            // 处理需要更新的文件
            if (filesToProcess.Count > 0)
            {
                _logger.Information("需要处理 {Count} 个新增/修改的文件", filesToProcess.Count);

                foreach (var filePath in filesToProcess)
                {
                    try
                    {
                        var fullPath = GetFullPathSecure(filePath);
                        var content = await File.ReadAllTextAsync(fullPath);
                        var hash = VectorStoreService.ComputeFileHash(content);

                        var chunks = SplitIntoChunks(content, ChunkSize);
                        if (chunks.Count == 0) continue;

                        var embeddings = await _embeddingService!.GenerateEmbeddingsAsync(chunks);

                        var vectors = new List<(int Index, string ChunkText, float[] Embedding)>();
                        for (int i = 0; i < chunks.Count && i < embeddings.Count; i++)
                        {
                            if (embeddings[i] != null)
                            {
                                vectors.Add((i, chunks[i], embeddings[i]!));
                                _vectorCache.Add(new DocumentVector
                                {
                                    FilePath = filePath,
                                    ChunkIndex = i,
                                    ChunkText = chunks[i],
                                    Embedding = embeddings[i]!,
                                    FileHash = hash
                                });
                            }
                        }

                        // 保存到数据库
                        await _vectorStoreService.SaveVectorsAsync(filePath, hash, vectors);
                        _logger.Debug("处理文件 {FilePath}: {Count} 个向量", filePath, vectors.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "处理文件失败: {FilePath}", filePath);
                    }
                }
            }

            _vectorCacheInitialized = true;
            var stats = await _vectorStoreService.GetStatisticsAsync();
            _logger.Information("向量缓存初始化完成，{FileCount} 个文件，{VectorCount} 个向量",
                stats.FileCount, stats.VectorCount);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载向量缓存失败");
            _vectorCacheInitialized = false;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// 刷新向量缓存（在文件变更后调用）- 增量更新
    /// </summary>
    public async Task RefreshVectorCacheAsync()
    {
        // 重置初始化标志，下次搜索时会触发增量更新
        _vectorCacheInitialized = false;
    }

    /// <summary>
    /// 增量更新单个文件的向量
    /// </summary>
    private async Task UpdateFileVectorsAsync(string relativePath)
    {
        if (_embeddingService == null || !_embeddingService.IsConfigured) return;

        try
        {
            var fullPath = GetFullPathSecure(relativePath);

            if (!File.Exists(fullPath))
            {
                // 文件已删除，移除向量
                await _vectorStoreService.DeleteFileVectorsAsync(relativePath);
                _vectorCache.RemoveAll(v => v.FilePath == relativePath);
                _logger.Debug("删除文件向量: {FilePath}", relativePath);
                return;
            }

            var content = await File.ReadAllTextAsync(fullPath);
            var hash = VectorStoreService.ComputeFileHash(content);

            // 检查是否需要更新
            var statuses = await _vectorStoreService.GetFileStatusesAsync();
            if (statuses.TryGetValue(relativePath, out var status) && status.FileHash == hash)
            {
                return; // 文件未变更
            }

            // 移除旧向量
            _vectorCache.RemoveAll(v => v.FilePath == relativePath);

            // 生成新向量
            var chunks = SplitIntoChunks(content, ChunkSize);
            if (chunks.Count == 0) return;

            var embeddings = await _embeddingService.GenerateEmbeddingsAsync(chunks);

            var vectors = new List<(int Index, string ChunkText, float[] Embedding)>();
            for (int i = 0; i < chunks.Count && i < embeddings.Count; i++)
            {
                if (embeddings[i] != null)
                {
                    vectors.Add((i, chunks[i], embeddings[i]!));
                    _vectorCache.Add(new DocumentVector
                    {
                        FilePath = relativePath,
                        ChunkIndex = i,
                        ChunkText = chunks[i],
                        Embedding = embeddings[i]!,
                        FileHash = hash
                    });
                }
            }

            await _vectorStoreService.SaveVectorsAsync(relativePath, hash, vectors);
            _logger.Debug("增量更新文件向量: {FilePath}, {Count} 个向量", relativePath, vectors.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "增量更新文件向量失败: {FilePath}", relativePath);
        }
    }

    /// <summary>
    /// 文本分块
    /// </summary>
    private List<string> SplitIntoChunks(string text, int chunkSize)
    {
        var chunks = new List<string>();
        var lines = text.Split('\n');
        var currentChunk = new StringBuilder();

        foreach (var line in lines)
        {
            if (currentChunk.Length + line.Length > chunkSize && currentChunk.Length > 0)
            {
                chunks.Add(currentChunk.ToString().Trim());
                currentChunk.Clear();
            }
            currentChunk.AppendLine(line);
        }

        if (currentChunk.Length > 0)
        {
            chunks.Add(currentChunk.ToString().Trim());
        }

        return chunks.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
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

            // 异步增量更新向量
            _ = Task.Run(async () =>
            {
                try
                {
                    await UpdateFileVectorsAsync(relativePath);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "更新文件向量失败");
                }
            });

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
                          "如果内容有较大差异，请先使用 read_knowledge_file 查看当前文件内容。"
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
