using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Athena.UI.Services.GameMode;

/// <summary>交到你手上的成果是什么样的实物（设计稿 7.1 "收"）。</summary>
public enum PolisItemKind
{
    /// <summary>卷轴：文档（报告、说明……）。</summary>
    Scroll,
    /// <summary>账册：表格。</summary>
    Ledger,
    /// <summary>彩绘板：图片。</summary>
    Painting,
    /// <summary>回答本身：没有落成文件的一段文字，在卷轴阅读器里读。</summary>
    Answer
}

/// <summary>一件成果的处理状态。只有 <see cref="Pending"/> 带颜色（"轮到你了"，设计稿第 8 节）。</summary>
public enum PolisItemState
{
    Pending,
    Accepted,
    Returned
}

/// <summary>文件的内容指纹：大小 + SHA-256。文件太大不值得整份读时只有大小（<see cref="Sha256"/> 为 null）。</summary>
public sealed record PolisFingerprint(long Size, string? Sha256)
{
    public bool Matches(PolisFingerprint? other)
        => other != null
           && other.Size == Size
           && (Sha256 == null || other.Sha256 == null
               ? Sha256 == null && other.Sha256 == null
               : string.Equals(Sha256, other.Sha256, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 一件藏品：一次真实的产出（设计稿原则 1）。指向的文件用工作区相对路径 + 内容指纹 + 来源消息 Id 记下；
/// 藏品存在游戏存档里，不碰工作区——"拆方块"永远不会变成删文件。
/// </summary>
public sealed record PolisItem
{
    public required string Id { get; init; }
    public required PolisItemKind Kind { get; init; }
    /// <summary>显示名（《报告》），不是路径。</summary>
    public required string Title { get; init; }
    /// <summary>工作区相对路径，'/' 分隔；回答、城外的文件为 null。</summary>
    public string? RelativePath { get; init; }
    public PolisFingerprint? Fingerprint { get; init; }
    public required string ConversationId { get; init; }
    public required string SourceMessageId { get; init; }
    public required PolisItemState State { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
    /// <summary>交付之后文件内容被改过（比如在 Word 里改了报告）：物品挂上"刚被修改"的标记（11.2）。</summary>
    public bool ModifiedSinceDelivery { get; init; }
    /// <summary>
    /// 它指向的文件找不到了（按路径、按改名、按指纹都没找回）：保留记录、名字和原来的处理状态，显示为空底座，
    /// 由你决定是否移除，绝不自动删除（11.3）。文件回来了就自动复原。
    /// </summary>
    public bool Lost { get; init; }
}

/// <summary>玩家在这座城邦里的位置和镜头缩放（10.1"玩家进度"）。</summary>
public sealed record PolisPlayerProgress(double X, double Z, double? Zoom);

/// <summary>
/// 一座城邦的存档（<c>polis.json</c>）：布局账本、藏品、玩家进度。能从文件或会话推导出来的一律不存（10.1）。
/// <see cref="Quarantined"/> 是读不懂的记录原文：一件藏品的记录坏了不能连累整座城，但也不能悄悄丢掉——
/// 原样留在存档里，下次写回时还在，留给人去看。
/// </summary>
public sealed record PolisSaveDocument
{
    /// <summary>
    /// v1 是 M0 导出工具写的裸账本（<c>{schemaVersion:1, entries:[…]}</c>）；v2 加了藏品、玩家进度与隔离区。
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public PolisLedger Ledger { get; init; } = PolisLedger.Empty;
    public IReadOnlyList<PolisItem> Items { get; init; } = Array.Empty<PolisItem>();
    public PolisPlayerProgress? Player { get; init; }
    /// <summary>第一次进入、播过奠基揭幕的时间（5.4）。之后每次进入都直接恢复，不再播。</summary>
    public DateTimeOffset? FoundedAt { get; init; }
    public IReadOnlyList<JsonElement> Quarantined { get; init; } = Array.Empty<JsonElement>();

    public static PolisSaveDocument Empty { get; } = new();
}

/// <summary>读存档的结果：文档本身，以及读的过程中发生了什么（日志与测试都看这个）。</summary>
public sealed record PolisSaveReadResult(
    PolisSaveDocument Document,
    int MigratedFromVersion,
    int QuarantinedRecords,
    bool Unreadable,
    int? FutureVersion);

/// <summary>
/// 建筑汇总的快照（<c>index.json</c> 里的一条）：重启时先按它把城画出来，再拿新的扫描核对差异（10.3）。
/// <see cref="ChildNames"/> 是它的直接子项名字（有上限），离线改名时按名字集合的相似度配对（11.3）。
/// </summary>
public sealed record PolisIndexBuilding(
    string Key,
    PolisBuildingKind Kind,
    int FileCount,
    long TotalBytes,
    DateTimeOffset LastModifiedUtc,
    bool Incomplete,
    IReadOnlyList<string> ChildNames);

/// <summary>索引快照（<c>index.json</c>）：纯缓存，删了也不会出错，只是少一次"离开期间的变化"报告。</summary>
public sealed record PolisIndexDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public DateTimeOffset ScannedAtUtc { get; init; }
    public IReadOnlyList<PolisIndexBuilding> Buildings { get; init; } = Array.Empty<PolisIndexBuilding>();
    public PolisAgoraSummary? Agora { get; init; }
    public bool Truncated { get; init; }
    /// <summary>藏品所指文件的指纹（按藏品 Id）。</summary>
    public IReadOnlyDictionary<string, PolisFingerprint> ItemFingerprints { get; init; } = new Dictionary<string, PolisFingerprint>();
}

/// <summary>
/// 存档的文本格式：序列化、按记录隔离损坏的解析、版本迁移。纯函数，文件读写在 <see cref="PolisSaveStore"/>。
/// </summary>
public static class PolisSaveFormat
{
    /// <summary>每座建筑记下的子项名字上限：离线改名配对用，几百个名字足够认出一个文件夹。</summary>
    public const int MaxChildNamesPerBuilding = 200;

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize(PolisSaveDocument document)
        => JsonSerializer.Serialize(document with { SchemaVersion = PolisSaveDocument.CurrentSchemaVersion }, JsonOptions)
               .Replace("\r\n", "\n") + "\n";

    public static string SerializeIndex(PolisIndexDocument index)
        => JsonSerializer.Serialize(index with { SchemaVersion = PolisIndexDocument.CurrentSchemaVersion }, JsonOptions)
               .Replace("\r\n", "\n") + "\n";

    /// <summary>
    /// 解析一份存档。整份不是 JSON（截断、乱码）→ <c>Unreadable</c>，由存储层改读上一份完好的副本。
    /// 单条记录坏了（账本条目、藏品）→ 跳过它、原文进隔离区，其余照常读出。
    /// 版本比当前新（新版应用写的）→ <c>FutureVersion</c>：不迁移、不覆盖，存储层先把它另存一份再说。
    /// </summary>
    public static PolisSaveReadResult Parse(string json)
    {
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException)
        {
            return new PolisSaveReadResult(PolisSaveDocument.Empty, 0, 0, Unreadable: true, FutureVersion: null);
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new PolisSaveReadResult(PolisSaveDocument.Empty, 0, 0, Unreadable: true, FutureVersion: null);

            var version = root.TryGetProperty("schemaVersion", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
                ? n
                : root.TryGetProperty("entries", out _) ? 1 : 0;
            if (version > PolisSaveDocument.CurrentSchemaVersion)
                return new PolisSaveReadResult(PolisSaveDocument.Empty, 0, 0, Unreadable: false, FutureVersion: version);
            if (version < 1)
                return new PolisSaveReadResult(PolisSaveDocument.Empty, 0, 0, Unreadable: true, FutureVersion: null);

            var quarantined = new List<JsonElement>();
            var freshlyQuarantined = 0;
            // v1（M0 的裸账本）的条目就在根上；v2 在 ledger 里
            var ledgerHolder = version == 1 ? root : root.TryGetProperty("ledger", out var l) && l.ValueKind == JsonValueKind.Object ? l : default;
            var entries = new List<PolisLedgerEntry>();
            if (ledgerHolder.ValueKind == JsonValueKind.Object
                && ledgerHolder.TryGetProperty("entries", out var rawEntries)
                && rawEntries.ValueKind == JsonValueKind.Array)
            {
                foreach (var raw in rawEntries.EnumerateArray())
                {
                    if (TryReadLedgerEntry(raw, out var entry)) entries.Add(entry);
                    else
                    {
                        quarantined.Add(raw.Clone());
                        freshlyQuarantined++;
                    }
                }
            }

            var items = new List<PolisItem>();
            var itemIds = new HashSet<string>(StringComparer.Ordinal);
            if (version >= 2 && root.TryGetProperty("items", out var rawItems) && rawItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var raw in rawItems.EnumerateArray())
                {
                    // 同一件藏品出现两次（并发写坏的那种）：第一条算数，第二条进隔离区
                    if (TryReadItem(raw, out var item) && itemIds.Add(item.Id)) items.Add(item);
                    else
                    {
                        quarantined.Add(raw.Clone());
                        freshlyQuarantined++;
                    }
                }
            }

            if (version >= 2 && root.TryGetProperty("quarantined", out var rawQuarantine) && rawQuarantine.ValueKind == JsonValueKind.Array)
            {
                foreach (var raw in rawQuarantine.EnumerateArray()) quarantined.Add(raw.Clone());
            }

            PolisPlayerProgress? player = null;
            if (version >= 2 && root.TryGetProperty("player", out var rawPlayer) && rawPlayer.ValueKind == JsonValueKind.Object)
            {
                // 玩家位置坏了就回广场（10.1：丢了 = 回到广场），不值得隔离
                player = TryRead<PolisPlayerProgress>(rawPlayer, out var p) && double.IsFinite(p.X) && double.IsFinite(p.Z) ? p : null;
            }

            DateTimeOffset? foundedAt = null;
            if (version >= 2 && root.TryGetProperty("foundedAt", out var rawFounded) && rawFounded.ValueKind == JsonValueKind.String
                && rawFounded.TryGetDateTimeOffset(out var founded))
            {
                foundedAt = founded;
            }

            var document = new PolisSaveDocument
            {
                SchemaVersion = PolisSaveDocument.CurrentSchemaVersion,
                Ledger = new PolisLedger(PolisLedger.CurrentSchemaVersion, entries),
                Items = items,
                Player = player,
                // 从 M0 的裸账本迁过来时没有奠基时间：它已经有地块了，算作奠基过，不再播揭幕
                FoundedAt = foundedAt ?? (version == 1 && entries.Count > 0 ? DateTimeOffset.UnixEpoch : null),
                Quarantined = quarantined
            };
            return new PolisSaveReadResult(document, version, freshlyQuarantined, Unreadable: false, FutureVersion: null);
        }
    }

    /// <summary>索引快照是纯缓存：读不懂（或版本不认识）就当没有，下次扫描重建。</summary>
    public static PolisIndexDocument? ParseIndex(string json)
    {
        try
        {
            var index = JsonSerializer.Deserialize<PolisIndexDocument>(json, JsonOptions);
            if (index == null || index.SchemaVersion != PolisIndexDocument.CurrentSchemaVersion) return null;
            // 单条坏了（缺键、空名字）就丢掉那一条：它只是缓存
            var buildings = (index.Buildings ?? Array.Empty<PolisIndexBuilding>())
                .Where(b => b != null && !string.IsNullOrEmpty(b.Key))
                .Select(b => b with { ChildNames = b.ChildNames ?? Array.Empty<string>() })
                .GroupBy(b => b.Key, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();
            return index with
            {
                Buildings = buildings,
                ItemFingerprints = index.ItemFingerprints ?? new Dictionary<string, PolisFingerprint>()
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadLedgerEntry(JsonElement raw, out PolisLedgerEntry entry)
    {
        entry = null!;
        if (!TryRead<PolisLedgerEntry>(raw, out var read) || string.IsNullOrEmpty(read.Key) || read.Order < 0) return false;
        entry = read;
        return true;
    }

    private static bool TryReadItem(JsonElement raw, out PolisItem item)
    {
        item = null!;
        if (!TryRead<PolisItem>(raw, out var read)
            || string.IsNullOrWhiteSpace(read.Id)
            || string.IsNullOrWhiteSpace(read.ConversationId)
            || string.IsNullOrWhiteSpace(read.SourceMessageId)
            || read.Title == null
            || !Enum.IsDefined(read.Kind)
            || !Enum.IsDefined(read.State))
            return false;
        if (read.RelativePath != null && !PolisPaths.IsCleanRelativePath(read.RelativePath)) return false;
        item = read;
        return true;
    }

    private static bool TryRead<T>(JsonElement raw, out T value) where T : class
    {
        value = null!;
        if (raw.ValueKind != JsonValueKind.Object) return false;
        try
        {
            var read = raw.Deserialize<T>(JsonOptions);
            if (read == null) return false;
            value = read;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            // required 成员缺失、类型不对、枚举值不认识：这一条进隔离区，不影响别的记录
            return false;
        }
    }
}
