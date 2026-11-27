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
public sealed class ConversationArchiveStore : IConversationArchiveStore, IConversationDraftStore, IConversationDirectory, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // SQLite stores these small fields in the index. Opening an existing installation builds
    // the index once on the directory reader's worker. Later listings do not touch message JSON.
    private const string DirectoryMetadata = "json_extract(payload,'$.isPinned','$.runtimeStatus','$.forkedFromHistoryId','$.createdByCronTaskId','$.cronTaskRunId')";
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
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM conversations ORDER BY updated_at DESC";
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
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
            await SaveAsync(repairedItem).ConfigureAwait(false);
        }
        return items;
    }

    private Task<List<ArchivedConversation>>? _startupDirectory;

    // The normal desktop startup requests this once, before constructing its services
    // and window. The shell consumes the same task; later listings always see fresh data.
    internal void PrepareStartupDirectory(Action<ConversationHistoryItem> prepareHistory) => _startupDirectory ??= Task.Run(() =>
    {
        var entries = ReadDirectory();
        if (entries.Count != 0 && ReadById(entries[0].Id) is { } first)
        {
            prepareHistory(first);
            entries[0] = entries[0] with { Prepared = first };
        }
        return entries;
    });

    Task<List<ArchivedConversation>> IConversationDirectory.ReadDirectoryAsync() =>
        Interlocked.Exchange(ref _startupDirectory, null) ?? Task.Run(ReadDirectory);

    private List<ArchivedConversation> ReadDirectory()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            CREATE INDEX IF NOT EXISTS ix_conversation_directory
            ON conversations(updated_at DESC,id,conversation_id,workspace_id,parent_conversation_id,title,{DirectoryMetadata})
            WHERE json_valid(payload)
            """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"""
            SELECT id,conversation_id,workspace_id,parent_conversation_id,title,updated_at,{DirectoryMetadata}
            FROM conversations INDEXED BY ix_conversation_directory WHERE json_valid(payload) ORDER BY updated_at DESC
            """;
        using var reader = cmd.ExecuteReader();
        var entries = new List<ArchivedConversation>();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            try
            {
                using var metadata = JsonDocument.Parse(reader.GetString(6));
                var fields = metadata.RootElement;
                var pinned = fields[0].ValueKind == JsonValueKind.True ||
                    fields[0].ValueKind == JsonValueKind.Number && fields[0].GetInt32() != 0;
                entries.Add(new ArchivedConversation(id, reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(4), reader.GetDateTime(5).ToLocalTime(), pinned, fields[1].GetString() ?? "idle",
                    reader.IsDBNull(3) ? null : reader.GetString(3), fields[2].GetString(), fields[3].GetString(), fields[4].GetString(),
                    () => ReadById(id) ?? throw new InvalidOperationException($"Conversation {id} is no longer available."))
                    { OwnsReadMessages = true });
            }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
            {
                _logger.Warning(ex, "Skipping an unreadable archive directory entry {HistoryId}", id);
            }
        }
        return entries;
    }

    HashSet<string> IConversationDirectory.FindByContent(IReadOnlySet<string> historyIds, Func<string, bool> matchesText, CancellationToken cancellation)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        connection.CreateFunction("matches_message", (string? content) =>
        {
            cancellation.ThrowIfCancellationRequested();
            return !string.IsNullOrEmpty(content) && matchesText(content);
        });
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id FROM conversations WHERE id IN (SELECT value FROM json_each($ids)) AND json_valid(payload) AND EXISTS (
                SELECT 1 FROM json_each(payload,'$.messages') AS m WHERE m.type='object' AND (
                    matches_message(m.value ->> '$.content') OR EXISTS (
                        SELECT 1 FROM json_each(m.value,'$.segments') AS s
                        WHERE s.type='object' AND coalesce(s.value ->> '$.kind',0) IN (0,'Markdown')
                          AND (s.value ->> '$.text') IS NOT (m.value ->> '$.content')
                          AND matches_message(s.value ->> '$.text'))))
            """;
        cmd.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(historyIds));
        var matches = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) matches.Add(reader.GetString(0));
        }
        catch (SqliteException) when (cancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellation);
        }
        return matches;
    }

    public Task<ConversationHistoryItem?> LoadByIdAsync(string id) => Task.Run(() => ReadById(id));

    private ConversationHistoryItem? ReadById(string id)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM conversations WHERE id = $id";
            command.Parameters.AddWithValue("$id", ValidateId(id));
            if (command.ExecuteScalar() is not string payload) return null;
            var item = JsonSerializer.Deserialize<ConversationHistoryItem>(payload, JsonOptions);
            if (item != null && ConversationPersistenceRecovery.Repair(item))
            {
                _logger.Warning("Repaired compression session persistence invariant: {HistoryId}, Revision={Revision}", item.Id, item.Revision);
                _writeGate.Wait();
                try { SaveCore(item, advanceOnDrift: false); }
                finally { _writeGate.Release(); }
            }
            return item;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load conversation history: {Id}", id);
            return null;
        }
    }

    // 本文件每个 await 都必须 ConfigureAwait(false)。_writeGate 同时被这里的异步路径和
    // 草稿存储的同步 Save()/Delete()（_writeGate.Wait()）持有：续体一旦贴回 UI 线程，
    // 而 UI 线程正阻塞在 Wait() 上，异步持有者就永远拿不到线程去 Release——闸门再不打开，
    // 界面永久冻结。与 MainWindowViewModel.PersistSessionStateAsync 注释记载的退出死锁同形。
    public Task SaveAsync(ConversationHistoryItem item) =>
        SaveCoreAsync(item, advanceOnDrift: false);

    /// <summary>
    /// 普通保存通道：落库并返回真正写入的 revision（同时回写到 <paramref name="item"/>）。
    ///
    /// 与 <see cref="SaveAsync"/> 的唯一差别是「同 revision、不同 payload」的处置。流式正文是
    /// 就地追加的（<c>assistantMsg.Content += contentDelta</c>），一整轮回复期间没有任何集合
    /// 变化，revision 因此原地不动，而 payload 一直在变——所以「内存比磁盘新、revision 却相同」
    /// 在这条通道上不是冲突，而是必然出现的正常状态。此时推进一个 revision 再写，让存储层
    /// 「同 revision ⇒ 同 payload」这条不变量继续成立，而不是把它当成写入者打架。
    ///
    /// 真正的冲突仍然抛出：incoming &lt; stored 说明另一个写入者已经推进过 revision，覆盖它会
    /// 丢数据。压缩提交走 <see cref="SaveAsync"/>，因为它的 revision 是协议的一部分
    /// （BaseRevision + 1），被拒绝就必须作废重来，不能就地顺延。
    /// </summary>
    public Task<long> SaveLatestAsync(ConversationHistoryItem item) =>
        SaveCoreAsync(item, advanceOnDrift: true);

    private async Task<long> SaveCoreAsync(ConversationHistoryItem item, bool advanceOnDrift)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Microsoft.Data.Sqlite performs synchronous I/O. Serialization, hashing and the
            // write all belong off the UI thread, after reserving the writer in submission order.
            return await Task.Run(() => SaveCore(item, advanceOnDrift)).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private long SaveCore(ConversationHistoryItem item, bool advanceOnDrift)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // 至多重试一次：_writeGate 已经把进程内写入者串行化，一次顺延仍被拒绝
        // 说明有本进程之外的写入者，那就是真冲突，不该继续抬高 revision 硬写。
        for (var attempt = 0; ; attempt++)
        {
            var payload = JsonSerializer.Serialize(item, JsonOptions);
            var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            var affected = ExecuteUpsert(connection, item, payload, payloadHash);
            if (affected > 0) return item.Revision;

            var rejection = InspectRejectedWrite(connection, item.Id, item.Revision, payloadHash);
            switch (rejection.Kind)
            {
                case RejectedWriteKind.Idempotent:
                    return item.Revision;
                case RejectedWriteKind.Stale:
                    throw new ConversationRevisionConflictException(
                        $"Stale conversation revision {item.Revision} cannot overwrite {rejection.StoredRevision}.");
                default:
                    if (!advanceOnDrift || attempt > 0)
                        throw new ConversationRevisionConflictException(
                            $"Different conversation payloads cannot share revision {item.Revision}.");
                    item.Revision = rejection.StoredRevision + 1;
                    continue;
            }
        }
    }

    private static int ExecuteUpsert(
        SqliteConnection connection,
        ConversationHistoryItem item,
        string payload,
        string payloadHash)
    {
        using var command = connection.CreateCommand();
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
        return command.ExecuteNonQuery();
    }

    private enum RejectedWriteKind
    {
        /// <summary>同 revision 同 payload：这次写入是重复的，什么都不用做。</summary>
        Idempotent,
        /// <summary>incoming &lt; stored：别的写入者已经更新，覆盖会丢数据。</summary>
        Stale,
        /// <summary>同 revision 不同 payload：内存相对磁盘发生了未计入 revision 的漂移。</summary>
        Drift
    }

    private readonly record struct RejectedWrite(RejectedWriteKind Kind, long StoredRevision);

    private static RejectedWrite InspectRejectedWrite(
        SqliteConnection connection,
        string id,
        long incomingRevision,
        string incomingPayloadHash)
    {
        using var current = connection.CreateCommand();
        current.CommandText = "SELECT revision, payload_hash FROM conversations WHERE id = $id";
        current.Parameters.AddWithValue("$id", ValidateId(id));
        using var reader = current.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("Conversation write was rejected but the current row is missing.");
        }

        var storedRevision = reader.GetInt64(0);
        var storedHash = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        if (storedRevision == incomingRevision
            && string.Equals(storedHash, incomingPayloadHash, StringComparison.Ordinal))
        {
            return new RejectedWrite(RejectedWriteKind.Idempotent, storedRevision);
        }

        return incomingRevision < storedRevision
            ? new RejectedWrite(RejectedWriteKind.Stale, storedRevision)
            : new RejectedWrite(RejectedWriteKind.Drift, storedRevision);
    }

    public async Task DeleteAsync(string id)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM conversations WHERE id = $id";
            command.Parameters.AddWithValue("$id", ValidateId(id));
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
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
