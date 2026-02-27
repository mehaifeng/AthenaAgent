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
    Task SaveVectorsAsync(string filePath, string fileHash, List<(int Index, string ChunkText, float[] Embedding)> vectors);

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
    private readonly SemaphoreSlim _lock = new(1, 1);

    public VectorStoreService(ILogger logger)
    {
        _logger = logger.ForContext<VectorStoreService>();

        _dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Athena",
            "KnowledgeBase",
            "vectors.db"
        );

        // 确保目录存在
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public async Task InitializeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            var createTableSql = @"
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
                    embedding BLOB NOT NULL,
                    created_at TEXT DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE(file_path, chunk_index)
                );

                CREATE INDEX IF NOT EXISTS IX_document_vectors_file_path ON document_vectors(file_path);
                CREATE INDEX IF NOT EXISTS IX_file_status_file_hash ON file_status(file_hash);
            ";

            using var command = new SqliteCommand(createTableSql, connection);
            await command.ExecuteNonQueryAsync();

            _logger.Information("向量存储数据库初始化完成: {Path}", _dbPath);
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
                SELECT id, file_path, chunk_index, chunk_text, embedding
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
                    Embedding = embedding
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

    public async Task SaveVectorsAsync(string filePath, string fileHash, List<(int Index, string ChunkText, float[] Embedding)> vectors)
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

                // 插入新向量
                foreach (var (index, chunkText, embedding) in vectors)
                {
                    using var insertCommand = new SqliteCommand(
                        @"INSERT INTO document_vectors (file_path, chunk_index, chunk_text, embedding)
                          VALUES (@filePath, @chunkIndex, @chunkText, @embedding)",
                        connection, transaction);

                    insertCommand.Parameters.Add(new SqliteParameter("@filePath", filePath));
                    insertCommand.Parameters.Add(new SqliteParameter("@chunkIndex", index));
                    insertCommand.Parameters.Add(new SqliteParameter("@chunkText", chunkText));
                    insertCommand.Parameters.Add(new SqliteParameter("@embedding", FloatsToBytes(embedding)));

                    await insertCommand.ExecuteNonQueryAsync();
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
                _logger.Debug("保存 {Count} 个向量: {FilePath}", vectors.Count, filePath);
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
                    "DELETE FROM file_status WHERE file_path = @filePath", connection, transaction))
                {
                    command.Parameters.Add(new SqliteParameter("@filePath", filePath));
                    await command.ExecuteNonQueryAsync();
                }

                transaction.Commit();
                _logger.Debug("删除文件向量: {FilePath}", filePath);
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
                "DELETE FROM document_vectors; DELETE FROM file_status;",
                connection);
            await command.ExecuteNonQueryAsync();

            _logger.Information("已清除所有向量数据");
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
