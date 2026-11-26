using Athena.UI.Services.Interfaces;
using Microsoft.Data.Sqlite;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>
/// 文档向量记录（用于内存缓存）
/// </summary>
internal sealed class DocumentVector
{
    public string FilePath { get; set; } = string.Empty;
    public int ChunkIndex { get; set; }
    public string ChunkText { get; set; } = string.Empty;
    /// <summary>分块所属的标题路径面包屑（如「文档标题 &gt; 二级标题」），用于嵌入时的上下文增强。</summary>
    public string HeadingPath { get; set; } = string.Empty;
    public float[]? Embedding { get; set; }
}

/// <summary>
/// SQLite 向量存储服务实现
/// </summary>
internal sealed class VectorStoreService : IDisposable
{
    private readonly string _dbPath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// 存储层 schema 版本。结构变更时递增；启动检测到旧版本会丢弃并重建（向量为派生数据，可从 .md 安全重建）。
    /// </summary>
    private const int SchemaVersion = 3;

    public VectorStoreService(ILogger logger, IPlatformPathService? pathService = null)
    {
        _logger = logger.ForContext<VectorStoreService>();
        if (pathService != null)
        {
            _dbPath = pathService.GetVectorStoreFilePath();
        }
        else
        {
            // 兜底逻辑
            _dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Athena",
                "KnowledgeBase",
                "vectors.db"
            );
        }

        // 确保目录存在
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _logger.Information("Vector store initialized at {Path}", _dbPath);
    }

    public async Task InitializeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            // Schema 迁移：版本不一致则丢弃旧结构重建（向量是派生数据，可从 .md 重新索引）
            var userVersion = 0;
            using (var pragmaRead = new SqliteCommand("PRAGMA user_version", connection))
            {
                userVersion = Convert.ToInt32(await pragmaRead.ExecuteScalarAsync());
            }

            if (userVersion != SchemaVersion)
            {
                _logger.Information("Vector store schema version {Old} -> {New}; discarding old structure and rebuilding", userVersion, SchemaVersion);
                using var dropCmd = new SqliteCommand(
                    @"DROP TABLE IF EXISTS document_vectors;
                      DROP TABLE IF EXISTS file_status;
                      DROP TABLE IF EXISTS vector_status;
                      DROP TABLE IF EXISTS fts_index;
                      DROP TABLE IF EXISTS embed_meta;", connection);
                await dropCmd.ExecuteNonQueryAsync();
            }

            // trigram 分词器对中文/代码做子串匹配，弥补默认 unicode61 不切中文词的缺陷
            var createTableSql = @"
                CREATE TABLE IF NOT EXISTS embed_meta (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    model TEXT NOT NULL,
                    dimension INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS file_status (
                    file_path TEXT PRIMARY KEY,
                    file_hash TEXT NOT NULL,
                    chunk_count INTEGER NOT NULL,
                    last_updated TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS vector_status (
                    file_path TEXT PRIMARY KEY,
                    file_hash TEXT NOT NULL,
                    chunk_count INTEGER NOT NULL,
                    last_updated TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS document_vectors (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    file_path TEXT NOT NULL,
                    chunk_index INTEGER NOT NULL,
                    chunk_text TEXT NOT NULL,
                    heading_path TEXT NOT NULL DEFAULT '',
                    embedding BLOB NULL,
                    created_at TEXT DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE(file_path, chunk_index)
                );

                CREATE VIRTUAL TABLE IF NOT EXISTS fts_index USING fts5(
                    file_path,
                    chunk_index UNINDEXED,
                    content,
                    tokenize = 'trigram'
                );

                CREATE INDEX IF NOT EXISTS IX_document_vectors_file_path ON document_vectors(file_path);
                CREATE INDEX IF NOT EXISTS IX_file_status_file_hash ON file_status(file_hash);
                CREATE INDEX IF NOT EXISTS IX_vector_status_file_hash ON vector_status(file_hash);
            ";

            using (var command = new SqliteCommand(createTableSql, connection))
            {
                await command.ExecuteNonQueryAsync();
            }

            using (var setVersion = new SqliteCommand($"PRAGMA user_version = {SchemaVersion}", connection))
            {
                await setVersion.ExecuteNonQueryAsync();
            }

            _logger.Information("Vector store and full-text index database initialized: {Path}", _dbPath);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>读取本地文本分块及已生成的向量。</summary>
    public async Task<List<DocumentVector>> LoadIndexedDocumentsAsync()
    {
        var vectors = new List<DocumentVector>();

        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            var sql = @"
                SELECT file_path, chunk_index, chunk_text, embedding, heading_path
                FROM document_vectors
                ORDER BY file_path, chunk_index
            ";

            using var command = new SqliteCommand(sql, connection);
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var embedding = reader.IsDBNull(3) ? null : BytesToFloats((byte[])reader[3]);

                vectors.Add(new DocumentVector
                {
                    FilePath = reader.GetString(0),
                    ChunkIndex = reader.GetInt32(1),
                    ChunkText = reader.GetString(2),
                    Embedding = embedding,
                    HeadingPath = reader.IsDBNull(4) ? string.Empty : reader.GetString(4)
                });
            }

            _logger.Information("Loaded {Count} indexed chunk(s) from database", vectors.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load indexed chunks");
        }
        finally
        {
            _lock.Release();
        }

        return vectors;
    }

    /// <summary>
    /// 全文本关键字检索
    /// </summary>
    public async Task<List<(string FilePath, int ChunkIndex, string Content, double Score)>> SearchFtsAsync(string query, int maxResults = 10)
    {
        var spec = BuildFtsQuerySpec(query);
        if (spec.MatchExpression == null && spec.ShortTerms.Count == 0)
            return new List<(string FilePath, int ChunkIndex, string Content, double Score)>();

        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            var candidates = new Dictionary<(string FilePath, int ChunkIndex), FtsCandidate>();

            if (spec.MatchExpression != null)
            {
                using var command = new SqliteCommand(@"
                    SELECT file_path, chunk_index, content, rank
                    FROM fts_index
                    WHERE content MATCH @query
                    ORDER BY rank
                    LIMIT @limit", connection);
                command.Parameters.AddWithValue("@query", spec.MatchExpression);
                command.Parameters.AddWithValue("@limit", Math.Max(maxResults * 4, maxResults));

                using var reader = await command.ExecuteReaderAsync();
                var rank = 0;
                while (await reader.ReadAsync())
                {
                    var key = (reader.GetString(0), reader.GetInt32(1));
                    candidates[key] = new FtsCandidate(reader.GetString(2), rank++, 0, -reader.GetDouble(3));
                }
            }

            if (spec.ShortTerms.Count > 0)
            {
                var hitParts = spec.ShortTerms.Select((_, i) =>
                    $"CASE WHEN content LIKE @short{i} ESCAPE '\\' THEN 1 ELSE 0 END").ToList();
                var hitExpression = string.Join(" + ", hitParts);
                var likeWhere = string.Join(" OR ", spec.ShortTerms.Select((_, i) =>
                    $"content LIKE @short{i} ESCAPE '\\'"));
                var sql = $@"
                    SELECT file_path, chunk_index, content, ({hitExpression}) AS short_hits
                    FROM fts_index
                    WHERE {likeWhere}
                    ORDER BY short_hits DESC, length(content)
                    LIMIT @limit";

                using var command = new SqliteCommand(sql, connection);
                for (var i = 0; i < spec.ShortTerms.Count; i++)
                    command.Parameters.AddWithValue($"@short{i}", $"%{EscapeLike(spec.ShortTerms[i])}%");
                command.Parameters.AddWithValue("@limit", Math.Max(maxResults * 4, maxResults));

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var key = (reader.GetString(0), reader.GetInt32(1));
                    var shortHits = reader.GetInt32(3);
                    if (candidates.TryGetValue(key, out var existing))
                        candidates[key] = existing with { ShortHits = shortHits };
                    else
                        candidates[key] = new FtsCandidate(reader.GetString(2), null, shortHits, shortHits);
                }
            }

            return candidates
                .OrderByDescending(item => item.Value.ShortHits)
                .ThenBy(item => item.Value.MatchRank ?? int.MaxValue)
                .ThenByDescending(item => item.Value.Score)
                .Take(maxResults)
                .Select(item => (
                    item.Key.FilePath,
                    item.Key.ChunkIndex,
                    item.Value.Content,
                    item.Value.Score))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "FTS search failed (Query: {Query})", query);
            return new List<(string FilePath, int ChunkIndex, string Content, double Score)>();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 为 trigram FTS 构造高召回查询。长词进入 MATCH；两字符中文词使用 LIKE 补召回，
    /// 避免“渲染 / 界面 / 更新”这类常见关键词因不足三个字符而完全失效。
    /// </summary>
    private static FtsQuerySpec BuildFtsQuerySpec(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return new FtsQuerySpec(null, new List<string>());

        static string Quote(string term) => "\"" + term.Replace("\"", " ").Trim() + "\"";

        var terms = query
            .Split(new[] { ' ', '\t', '\r', '\n', '，', '、', ',', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(term => term.Trim())
            .Where(term => term.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var matchTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shortTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in terms)
        {
            if (term.Length == 2)
                shortTerms.Add(term);

            if (term.Length >= 3)
                matchTerms.Add(term);

            // 对无空格的长复合查询增加滑动 trigram，容忍中英文间空格和局部措辞差异。
            if (term.Length >= 6)
            {
                for (var i = 0; i <= term.Length - 3; i++)
                    matchTerms.Add(term.Substring(i, 3));
            }
        }

        var matchExpression = matchTerms.Count == 0
            ? null
            : string.Join(" OR ", matchTerms.Select(Quote));
        return new FtsQuerySpec(matchExpression, shortTerms.ToList());
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private sealed record FtsQuerySpec(string? MatchExpression, List<string> ShortTerms);
    private sealed record FtsCandidate(string Content, int? MatchRank, int ShortHits, double Score);

    public Task<Dictionary<string, string>> GetTextFileHashesAsync()
        => ReadFileHashesAsync("SELECT file_path, file_hash FROM file_status");

    // Separate from the text index because partial embeddings are not marked complete.
    public Task<Dictionary<string, string>> GetVectorFileHashesAsync()
        => ReadFileHashesAsync("SELECT file_path, file_hash FROM vector_status");

    private async Task<Dictionary<string, string>> ReadFileHashesAsync(string query)
    {
        var hashes = new Dictionary<string, string>();
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            using var command = new SqliteCommand(query, connection);
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                hashes[reader.GetString(0)] = reader.GetString(1);
        }
        finally
        {
            _lock.Release();
        }

        return hashes;
    }

    public async Task SaveChunksAsync(
        string filePath,
        string fileHash,
        List<(int Index, string ChunkText, string HeadingPath)> chunks)
    {
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            using var transaction = connection.BeginTransaction();

            try
            {
                // 文本发生变化后，旧分块、FTS 和向量都失效；新的 FTS 不依赖 Embedding。
                using (var deleteCommand = new SqliteCommand(
                    "DELETE FROM document_vectors WHERE file_path = @filePath", connection, transaction))
                {
                    deleteCommand.Parameters.Add(new SqliteParameter("@filePath", filePath));
                    await deleteCommand.ExecuteNonQueryAsync();
                }

                // 删除旧 FTS 索引
                using (var deleteFtsCommand = new SqliteCommand(
                    "DELETE FROM fts_index WHERE file_path = @filePath", connection, transaction))
                {
                    deleteFtsCommand.Parameters.Add(new SqliteParameter("@filePath", filePath));
                    await deleteFtsCommand.ExecuteNonQueryAsync();
                }

                using (var deleteVectorStatusCommand = new SqliteCommand(
                    "DELETE FROM vector_status WHERE file_path = @filePath", connection, transaction))
                {
                    deleteVectorStatusCommand.Parameters.AddWithValue("@filePath", filePath);
                    await deleteVectorStatusCommand.ExecuteNonQueryAsync();
                }

                using var insert = new SqliteCommand("""
                    INSERT INTO document_vectors (file_path, chunk_index, chunk_text, heading_path, embedding)
                    VALUES (@path, @idx, @text, @heading, NULL)
                    """, connection, transaction);
                insert.Parameters.AddWithValue("@path", filePath);
                var chunkIndex = insert.Parameters.Add("@idx", SqliteType.Integer);
                var text = insert.Parameters.Add("@text", SqliteType.Text);
                var heading = insert.Parameters.Add("@heading", SqliteType.Text);
                using var fts = new SqliteCommand("INSERT INTO fts_index (file_path, chunk_index, content) VALUES (@path, @idx, @text)", connection, transaction);
                fts.Parameters.AddWithValue("@path", filePath);
                var ftsIndex = fts.Parameters.Add("@idx", SqliteType.Integer);
                var ftsText = fts.Parameters.Add("@text", SqliteType.Text);
                foreach (var (index, chunkText, headingPath) in chunks)
                {
                    chunkIndex.Value = ftsIndex.Value = index;
                    text.Value = ftsText.Value = chunkText;
                    heading.Value = headingPath ?? string.Empty;
                    await insert.ExecuteNonQueryAsync();
                    await fts.ExecuteNonQueryAsync();
                }

                using (var statusCommand = new SqliteCommand(
                    @"INSERT OR REPLACE INTO file_status (file_path, file_hash, chunk_count, last_updated)
                      VALUES (@filePath, @fileHash, @chunkCount, @lastUpdated)",
                    connection, transaction))
                {
                    statusCommand.Parameters.Add(new SqliteParameter("@filePath", filePath));
                    statusCommand.Parameters.Add(new SqliteParameter("@fileHash", fileHash));
                    statusCommand.Parameters.Add(new SqliteParameter("@chunkCount", chunks.Count));
                    statusCommand.Parameters.Add(new SqliteParameter("@lastUpdated", DateTime.UtcNow.ToString("O")));

                    await statusCommand.ExecuteNonQueryAsync();
                }

                transaction.Commit();
                _logger.Debug("Saved {Count} local text chunk(s) and FTS index: {FilePath}", chunks.Count, filePath);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveEmbeddingsAsync(
        string filePath,
        string fileHash,
        List<(int Index, float[] Embedding)> embeddings)
    {
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();

            try
            {
                using (var clearCommand = new SqliteCommand(
                    "UPDATE document_vectors SET embedding = NULL WHERE file_path = @filePath", connection, transaction))
                {
                    clearCommand.Parameters.AddWithValue("@filePath", filePath);
                    await clearCommand.ExecuteNonQueryAsync();
                }

                using var updateCommand = new SqliteCommand(
                    @"UPDATE document_vectors SET embedding = @embedding
                      WHERE file_path = @filePath AND chunk_index = @chunkIndex", connection, transaction);
                updateCommand.Parameters.AddWithValue("@filePath", filePath);
                var chunkIndex = updateCommand.Parameters.Add("@chunkIndex", SqliteType.Integer);
                var vector = updateCommand.Parameters.Add("@embedding", SqliteType.Blob);
                foreach (var (index, embedding) in embeddings)
                {
                    chunkIndex.Value = index;
                    vector.Value = FloatsToBytes(embedding);
                    await updateCommand.ExecuteNonQueryAsync();
                }

                int chunkCount;
                using (var countCommand = new SqliteCommand(
                    "SELECT chunk_count FROM file_status WHERE file_path = @filePath", connection, transaction))
                {
                    countCommand.Parameters.AddWithValue("@filePath", filePath);
                    chunkCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync());
                }

                if (embeddings.Count == chunkCount)
                {
                    using var statusCommand = new SqliteCommand(
                        @"INSERT OR REPLACE INTO vector_status (file_path, file_hash, chunk_count, last_updated)
                          VALUES (@filePath, @fileHash, @chunkCount, @lastUpdated)", connection, transaction);
                    statusCommand.Parameters.AddWithValue("@filePath", filePath);
                    statusCommand.Parameters.AddWithValue("@fileHash", fileHash);
                    statusCommand.Parameters.AddWithValue("@chunkCount", chunkCount);
                    statusCommand.Parameters.AddWithValue("@lastUpdated", DateTime.UtcNow.ToString("O"));
                    await statusCommand.ExecuteNonQueryAsync();
                }
                else
                {
                    using var clearStatusCommand = new SqliteCommand(
                        "DELETE FROM vector_status WHERE file_path = @filePath", connection, transaction);
                    clearStatusCommand.Parameters.AddWithValue("@filePath", filePath);
                    await clearStatusCommand.ExecuteNonQueryAsync();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteFileIndexAsync(string filePath)
    {
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            using var transaction = connection.BeginTransaction();

            try
            {
                using (var command = new SqliteCommand(
                    "DELETE FROM document_vectors WHERE file_path = @filePath", connection, transaction))
                {
                    command.Parameters.Add(new SqliteParameter("@filePath", filePath));
                    await command.ExecuteNonQueryAsync();
                }

                using (var command = new SqliteCommand(
                    "DELETE FROM fts_index WHERE file_path = @filePath", connection, transaction))
                {
                    command.Parameters.Add(new SqliteParameter("@filePath", filePath));
                    await command.ExecuteNonQueryAsync();
                }

                using (var command = new SqliteCommand(
                    "DELETE FROM file_status WHERE file_path = @filePath", connection, transaction))
                {
                    command.Parameters.Add(new SqliteParameter("@filePath", filePath));
                    await command.ExecuteNonQueryAsync();
                }

                using (var command = new SqliteCommand(
                    "DELETE FROM vector_status WHERE file_path = @filePath", connection, transaction))
                {
                    command.Parameters.AddWithValue("@filePath", filePath);
                    await command.ExecuteNonQueryAsync();
                }

                transaction.Commit();
                _logger.Debug("Deleted file vector and full-text index: {FilePath}", filePath);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearEmbeddingsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            using var command = new SqliteCommand(
                "UPDATE document_vectors SET embedding = NULL; DELETE FROM vector_status; DELETE FROM embed_meta;",
                connection);
            await command.ExecuteNonQueryAsync();

            _logger.Information("All vectors cleared; keeping local chunks and FTS index");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(int FileCount, int VectorCount)> GetStatisticsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            int fileCount = 0;
            int vectorCount = 0;

            using (var command = new SqliteCommand("SELECT COUNT(*) FROM file_status", connection))
            {
                fileCount = Convert.ToInt32(await command.ExecuteScalarAsync());
            }

            using (var command = new SqliteCommand(
                "SELECT COUNT(*) FROM document_vectors WHERE embedding IS NOT NULL", connection))
            {
                vectorCount = Convert.ToInt32(await command.ExecuteScalarAsync());
            }

            return (fileCount, vectorCount);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(string Model, int Dimension)?> GetEmbeddingFingerprintAsync()
    {
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            using var command = new SqliteCommand("SELECT model, dimension FROM embed_meta WHERE id = 1", connection);
            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return (reader.GetString(0), reader.GetInt32(1));
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to read embedding model fingerprint");
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetEmbeddingFingerprintAsync(string model, int dimension)
    {
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            using var command = new SqliteCommand(
                @"INSERT OR REPLACE INTO embed_meta (id, model, dimension) VALUES (1, @model, @dim)", connection);
            command.Parameters.AddWithValue("@model", model);
            command.Parameters.AddWithValue("@dim", dimension);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 计算文件内容哈希
    /// </summary>
    public static string ComputeFileHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// float[] 转 byte[]
    /// </summary>
    private static byte[] FloatsToBytes(float[] floats)
    {
        var bytes = new byte[floats.Length * sizeof(float)];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>
    /// byte[] 转 float[]
    /// </summary>
    private static float[] BytesToFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    public void Dispose() => _lock.Dispose();
}
