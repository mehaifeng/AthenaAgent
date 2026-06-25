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
public class DocumentVector
{
    public int Id { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public int ChunkIndex { get; set; }
    public string ChunkText { get; set; } = string.Empty;
    /// <summary>分块所属的标题路径面包屑（如「文档标题 &gt; 二级标题」），用于嵌入时的上下文增强。</summary>
    public string HeadingPath { get; set; } = string.Empty;
    public float[]? Embedding { get; set; }
    public string FileHash { get; set; } = string.Empty;
}

/// <summary>
/// 文件状态记录
/// </summary>
public class FileStatus
{
    public string FilePath { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public int ChunkCount { get; set; }
    public DateTime LastUpdated { get; set; }
}

/// <summary>
/// 向量存储服务接口
/// </summary>
public interface IVectorStoreService
{
    /// <summary>
    /// 初始化数据库
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// 全文本关键字检索（BM25），返回结果按相关性升序（rank 越小越相关，已取反为正向分）。
    /// </summary>
    Task<List<(string FilePath, int ChunkIndex, string Content, double Score)>> SearchFtsAsync(string query, int maxResults = 10);

    /// <summary>
    /// 加载所有向量到内存
    /// </summary>
    Task<List<DocumentVector>> LoadAllVectorsAsync();

    /// <summary>
    /// 获取所有文件状态
    /// </summary>
    Task<Dictionary<string, FileStatus>> GetFileStatusesAsync();

    /// <summary>
    /// 保存向量（批量）
    /// </summary>
    Task SaveVectorsAsync(string filePath, string fileHash, List<(int Index, string ChunkText, string HeadingPath, float[] Embedding)> vectors);

    /// <summary>
    /// 读取向量库的嵌入模型指纹（模型标识 + 维度）。无记录返回 null。
    /// </summary>
    Task<(string Model, int Dimension)?> GetEmbeddingFingerprintAsync();

    /// <summary>
    /// 写入向量库的嵌入模型指纹。
    /// </summary>
    Task SetEmbeddingFingerprintAsync(string model, int dimension);

    /// <summary>
    /// 删除文件的向量
    /// </summary>
    Task DeleteFileVectorsAsync(string filePath);

    /// <summary>
    /// 清除所有向量
    /// </summary>
    Task ClearAllAsync();

    /// <summary>
    /// 获取统计信息
    /// </summary>
    Task<(int FileCount, int VectorCount)> GetStatisticsAsync();
}

/// <summary>
/// SQLite 向量存储服务实现
/// </summary>
public class VectorStoreService : IVectorStoreService
{
    private readonly string _dbPath;
    private readonly ILogger _logger;
    private readonly IPlatformPathService? _pathService;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// 存储层 schema 版本。结构变更时递增；启动检测到旧版本会丢弃并重建（向量为派生数据，可从 .md 安全重建）。
    /// </summary>
    private const int SchemaVersion = 2;

    public VectorStoreService(ILogger logger, IPlatformPathService? pathService = null)
    {
        _logger = logger.ForContext<VectorStoreService>();
        _pathService = pathService;

        if (_pathService != null)
        {
            _dbPath = _pathService.GetVectorStoreFilePath();
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
                _logger.Information("向量库 schema 版本 {Old} -> {New}，丢弃旧结构并重建", userVersion, SchemaVersion);
                using var dropCmd = new SqliteCommand(
                    @"DROP TABLE IF EXISTS document_vectors;
                      DROP TABLE IF EXISTS file_status;
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

                CREATE TABLE IF NOT EXISTS document_vectors (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    file_path TEXT NOT NULL,
                    chunk_index INTEGER NOT NULL,
                    chunk_text TEXT NOT NULL,
                    heading_path TEXT NOT NULL DEFAULT '',
                    embedding BLOB NOT NULL,
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
            ";

            using (var command = new SqliteCommand(createTableSql, connection))
            {
                await command.ExecuteNonQueryAsync();
            }

            using (var setVersion = new SqliteCommand($"PRAGMA user_version = {SchemaVersion}", connection))
            {
                await setVersion.ExecuteNonQueryAsync();
            }

            _logger.Information("向量存储及全文本索引数据库初始化完成: {Path}", _dbPath);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<DocumentVector>> LoadAllVectorsAsync()
    {
        var vectors = new List<DocumentVector>();

        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            var sql = @"
                SELECT id, file_path, chunk_index, chunk_text, embedding, heading_path
                FROM document_vectors
                ORDER BY file_path, chunk_index
            ";

            using var command = new SqliteCommand(sql, connection);
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var embeddingBlob = (byte[])reader[4];
                var embedding = BytesToFloats(embeddingBlob);

                vectors.Add(new DocumentVector
                {
                    Id = reader.GetInt32(0),
                    FilePath = reader.GetString(1),
                    ChunkIndex = reader.GetInt32(2),
                    ChunkText = reader.GetString(3),
                    Embedding = embedding,
                    HeadingPath = reader.IsDBNull(5) ? string.Empty : reader.GetString(5)
                });
            }

            _logger.Information("从数据库加载 {Count} 个向量", vectors.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载向量失败");
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
        var results = new List<(string FilePath, int ChunkIndex, string Content, double Score)>();

        var matchExpr = BuildFtsMatchExpression(query);
        if (matchExpr == null) return results;

        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            // 使用 FTS5 的 rank 进行评分（BM25，越小越相关，这里取负值转为正向分）
            var sql = @"
                SELECT file_path, chunk_index, content, rank
                FROM fts_index
                WHERE content MATCH @query
                ORDER BY rank
                LIMIT @limit";

            using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("@query", matchExpr);
            command.Parameters.AddWithValue("@limit", maxResults);

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add((
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    -reader.GetDouble(3) // 转换为正数
                ));
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "FTS 搜索失败 (Query: {Query})", query);
        }
        finally
        {
            _lock.Release();
        }

        return results;
    }

    /// <summary>
    /// 构造 FTS5 MATCH 表达式：按空白切词，各词加引号后用 OR 连接，实现“命中任一关键词”。
    /// trigram 分词下每个查询词需 ≥3 字符才能命中，过短词被过滤；全部过滤后回退整串。
    /// 返回 null 表示无可用查询。
    /// </summary>
    private static string? BuildFtsMatchExpression(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        static string Quote(string term) => "\"" + term.Replace("\"", " ").Trim() + "\"";

        var terms = query
            .Split(new[] { ' ', '\t', '\r', '\n', '，', '、', ',', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3)
            .Distinct()
            .ToList();

        if (terms.Count > 0)
            return string.Join(" OR ", terms.Select(Quote));

        // 无满足长度的词（如纯短词查询）：用整串做子串匹配
        var whole = query.Trim();
        return whole.Length >= 3 ? Quote(whole) : null;
    }

    public async Task<Dictionary<string, FileStatus>> GetFileStatusesAsync()
    {
        var statuses = new Dictionary<string, FileStatus>();

        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            var sql = "SELECT file_path, file_hash, chunk_count, last_updated FROM file_status";

            using var command = new SqliteCommand(sql, connection);
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var filePath = reader.GetString(0);
                statuses[filePath] = new FileStatus
                {
                    FilePath = filePath,
                    FileHash = reader.GetString(1),
                    ChunkCount = reader.GetInt32(2),
                    LastUpdated = DateTime.Parse(reader.GetString(3))
                };
            }
        }
        finally
        {
            _lock.Release();
        }

        return statuses;
    }

    public async Task SaveVectorsAsync(string filePath, string fileHash, List<(int Index, string ChunkText, string HeadingPath, float[] Embedding)> vectors)
    {
        if (vectors.Count == 0) return;

        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            using var transaction = connection.BeginTransaction();

            try
            {
                // 删除旧向量
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

                // 插入新向量和 FTS 索引
                foreach (var (index, chunkText, headingPath, embedding) in vectors)
                {
                    using var insertCommand = new SqliteCommand(
                        @"INSERT INTO document_vectors (file_path, chunk_index, chunk_text, heading_path, embedding)
                          VALUES (@filePath, @chunkIndex, @chunkText, @headingPath, @embedding)",
                        connection, transaction);

                    insertCommand.Parameters.Add(new SqliteParameter("@filePath", filePath));
                    insertCommand.Parameters.Add(new SqliteParameter("@chunkIndex", index));
                    insertCommand.Parameters.Add(new SqliteParameter("@chunkText", chunkText));
                    insertCommand.Parameters.Add(new SqliteParameter("@headingPath", headingPath ?? string.Empty));
                    insertCommand.Parameters.Add(new SqliteParameter("@embedding", FloatsToBytes(embedding)));

                    await insertCommand.ExecuteNonQueryAsync();

                    // FTS 索引
                    using var insertFtsCommand = new SqliteCommand(
                        @"INSERT INTO fts_index (file_path, chunk_index, content)
                          VALUES (@filePath, @chunkIndex, @content)",
                        connection, transaction);

                    insertFtsCommand.Parameters.Add(new SqliteParameter("@filePath", filePath));
                    insertFtsCommand.Parameters.Add(new SqliteParameter("@chunkIndex", index));
                    insertFtsCommand.Parameters.Add(new SqliteParameter("@content", chunkText));

                    await insertFtsCommand.ExecuteNonQueryAsync();
                }

                // 更新文件状态
                using (var statusCommand = new SqliteCommand(
                    @"INSERT OR REPLACE INTO file_status (file_path, file_hash, chunk_count, last_updated)
                      VALUES (@filePath, @fileHash, @chunkCount, @lastUpdated)",
                    connection, transaction))
                {
                    statusCommand.Parameters.Add(new SqliteParameter("@filePath", filePath));
                    statusCommand.Parameters.Add(new SqliteParameter("@fileHash", fileHash));
                    statusCommand.Parameters.Add(new SqliteParameter("@chunkCount", vectors.Count));
                    statusCommand.Parameters.Add(new SqliteParameter("@lastUpdated", DateTime.UtcNow.ToString("O")));

                    await statusCommand.ExecuteNonQueryAsync();
                }

                transaction.Commit();
                _logger.Debug("保存 {Count} 个向量及全文本索引: {FilePath}", vectors.Count, filePath);
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

    public async Task DeleteFileVectorsAsync(string filePath)
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

                transaction.Commit();
                _logger.Debug("删除文件向量及全文本索引: {FilePath}", filePath);
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

    public async Task ClearAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            using var command = new SqliteCommand(
                "DELETE FROM document_vectors; DELETE FROM fts_index; DELETE FROM file_status;",
                connection);
            await command.ExecuteNonQueryAsync();

            _logger.Information("已清除所有向量及全文本索引数据");
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

            using (var command = new SqliteCommand("SELECT COUNT(*) FROM document_vectors", connection))
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
            _logger.Warning(ex, "读取嵌入模型指纹失败");
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
}
