using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Microsoft.Data.Sqlite;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>SQLite 对话存储；消息 JSON 保留完整领域结构，索引字段独立列出。</summary>
public sealed class ConversationArchiveStore : IConversationArchiveStore, IConversationDraftStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _connectionString;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    public ConversationArchiveStore(IPlatformPathService platformPathService, ILogger logger)
    {
        var appDataDirectory = platformPathService.GetAppDataDirectory();
        Directory.CreateDirectory(appDataDirectory);
        var databasePath = Path.Combine(appDataDirectory, "conversations.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        _logger = logger.ForContext<ConversationArchiveStore>();
        Initialize();
    }

    public async Task<List<ConversationHistoryItem>> LoadAllAsync()
    {
        var items = new List<ConversationHistoryItem>();
        var repairedItems = new List<ConversationHistoryItem>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM conversations ORDER BY updated_at DESC";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            try
            {
                var item = JsonSerializer.Deserialize<ConversationHistoryItem>(reader.GetString(0), JsonOptions);
                if (item == null) continue;
                if (item.Messages.Count > 0) item.MessageCount = item.Messages.Count(IsCountableMessage);
                if (ConversationPersistenceRecovery.Repair(item))
                {
                    repairedItems.Add(item);
                    _logger.Warning("Idempotently repaired compression session persistence invariant: {HistoryId}, Revision={Revision}", item.Id, item.Revision);
                }
                items.Add(item);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to read conversation record");
            }
        }

        foreach (var repairedItem in repairedItems)
        {
            await SaveAsync(repairedItem);
        }
        return items;
    }

    public async Task<ConversationHistoryItem?> LoadByIdAsync(string id)
    {
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM conversations WHERE id = $id";
            command.Parameters.AddWithValue("$id", ValidateId(id));
            var payload = await command.ExecuteScalarAsync() as string;
            if (payload == null) return null;
            var item = JsonSerializer.Deserialize<ConversationHistoryItem>(payload, JsonOptions);
            if (item != null && ConversationPersistenceRecovery.Repair(item))
            {
                _logger.Warning("Idempotently repaired compression session persistence invariant: {HistoryId}, Revision={Revision}", item.Id, item.Revision);
                await SaveAsync(item);
            }
            return item;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load conversation history: {Id}", id);
            return null;
        }
    }

    public async Task SaveAsync(ConversationHistoryItem item)
    {
        await _writeGate.WaitAsync();
        try
        {
            var payload = JsonSerializer.Serialize(item, JsonOptions);
            var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO conversations (id, conversation_id, workspace_id, parent_conversation_id, title, created_at, updated_at, revision, payload_hash, payload)
                VALUES ($id, $conversationId, $workspaceId, $parentId, $title, $createdAt, $updatedAt, $revision, $payloadHash, $payload)
                ON CONFLICT(id) DO UPDATE SET
                    conversation_id = excluded.conversation_id,
                    workspace_id = excluded.workspace_id,
                    parent_conversation_id = excluded.parent_conversation_id,
                    title = excluded.title,
                    updated_at = excluded.updated_at,
                    revision = excluded.revision,
                    payload_hash = excluded.payload_hash,
                    payload = excluded.payload
                WHERE excluded.revision > conversations.revision
                """;
            command.Parameters.AddWithValue("$id", ValidateId(item.Id));
            command.Parameters.AddWithValue("$conversationId", item.ConversationId);
            command.Parameters.AddWithValue("$workspaceId", (object?)item.WorkspaceId ?? DBNull.Value);
            command.Parameters.AddWithValue("$parentId", (object?)item.ForkedFromConversationId ?? DBNull.Value);
            command.Parameters.AddWithValue("$title", item.Summary);
            command.Parameters.AddWithValue("$createdAt", item.CreatedAt.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$updatedAt", item.UpdatedAt.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$revision", item.Revision);
            command.Parameters.AddWithValue("$payloadHash", payloadHash);
            command.Parameters.AddWithValue("$payload", payload);
            var affected = await command.ExecuteNonQueryAsync();
            if (affected == 0)
            {
                await ValidateRejectedWriteAsync(connection, item.Id, item.Revision, payloadHash);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task ValidateRejectedWriteAsync(
        SqliteConnection connection,
        string id,
        long incomingRevision,
        string incomingPayloadHash)
    {
        await using var current = connection.CreateCommand();
        current.CommandText = "SELECT revision, payload_hash FROM conversations WHERE id = $id";
        current.Parameters.AddWithValue("$id", ValidateId(id));
        await using var reader = await current.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Conversation write was rejected but the current row is missing.");
        }

        var storedRevision = reader.GetInt64(0);
        var storedHash = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        if (storedRevision == incomingRevision
            && string.Equals(storedHash, incomingPayloadHash, StringComparison.Ordinal))
        {
            return;
        }

        if (incomingRevision < storedRevision)
        {
            throw new ConversationRevisionConflictException(
                $"Stale conversation revision {incomingRevision} cannot overwrite {storedRevision}.");
        }

        throw new ConversationRevisionConflictException(
            $"Different conversation payloads cannot share revision {incomingRevision}.");
    }

    public async Task DeleteAsync(string id)
    {
        await _writeGate.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM conversations WHERE id = $id";
            command.Parameters.AddWithValue("$id", ValidateId(id));
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Save(ConversationDraftSnapshot snapshot)
    {
        _writeGate.Wait();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO app_state (key, payload) VALUES ('legacy-draft', $payload) ON CONFLICT(key) DO UPDATE SET payload = excluded.payload";
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(snapshot, JsonOptions));
            command.ExecuteNonQuery();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ConversationDraftSnapshot? Load()
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM app_state WHERE key = 'legacy-draft'";
            var payload = command.ExecuteScalar() as string;
            return payload == null ? null : JsonSerializer.Deserialize<ConversationDraftSnapshot>(payload, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load main conversation draft");
            return null;
        }
    }

    public void Delete()
    {
        _writeGate.Wait();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM app_state WHERE key = 'legacy-draft'";
            command.ExecuteNonQuery();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public static bool IsCountableMessage(ChatMessage message)
        => message.Role == "user"
           || (message.Role == "assistant" && string.IsNullOrEmpty(message.ToolCallsJson));

    private static string ValidateId(string id)
    {
        if (!Guid.TryParse(id, out var parsed))
            throw new ArgumentException("Conversation history ID must be a GUID.", nameof(id));
        return parsed.ToString();
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS conversations (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                workspace_id TEXT NULL,
                parent_conversation_id TEXT NULL,
                title TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                revision INTEGER NOT NULL DEFAULT 0,
                payload_hash TEXT NULL,
                payload TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_conversations_workspace_updated
                ON conversations(workspace_id, updated_at DESC);
            CREATE INDEX IF NOT EXISTS ix_conversations_parent
                ON conversations(parent_conversation_id);
            CREATE TABLE IF NOT EXISTS app_state (
                key TEXT PRIMARY KEY,
                payload TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "conversations", "revision", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "conversations", "payload_hash", "TEXT NULL");
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table})";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writeGate.Dispose();
    }
}

public sealed class ConversationRevisionConflictException(string message) : InvalidOperationException(message);
