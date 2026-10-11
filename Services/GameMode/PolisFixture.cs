using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Athena.UI.Services.GameMode;

public sealed record PolisFixtureBuilding(
    string Key,
    PolisBuildingKind Kind,
    int Files,
    long Bytes,
    IReadOnlyDictionary<string, int> Kinds,
    DateTimeOffset LastModified,
    PolisBuildingState State,
    int SizeClass,
    bool Incomplete,
    PolisPlot Plot,
    int Order);

public sealed record PolisFixtureVacantPlot(string Key, PolisPlot Plot);

public sealed record PolisFixtureAgora(int Files, long Bytes, DateTimeOffset? LastModified, bool Incomplete);

public sealed record PolisFixtureScan(int Files, int Visited, bool Truncated, int SkippedDirectories, int UnreadableDirectories);

/// <summary>网页原型读的夹具：一座城邦的快照 + 一段回放。只有名字、数量、时间和地块，没有任何文件内容或消息正文。</summary>
public sealed record PolisFixtureDocument(
    int Schema,
    string Kind,
    bool Synthetic,
    DateTimeOffset GeneratedAt,
    string WorkspaceName,
    PolisFixtureScan Scan,
    PolisFixtureAgora Agora,
    IReadOnlyList<PolisFixtureBuilding> Buildings,
    IReadOnlyList<PolisFixtureVacantPlot> Vacant,
    IReadOnlyList<string> Market,
    IReadOnlyDictionary<string, PolisPlot> PublicSites,
    int SeaStartsAtZ,
    PolisReplay? Replay)
{
    public const int CurrentSchema = 1;
    public const string FixtureKind = "athena-polis-fixture";
}

public static class PolisFixture
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 夹具里有中文文件夹名；网页按 UTF-8 读，没必要转义成 \uXXXX
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// 工作区相对路径落在哪座建筑：第一段是某座建筑（或市集摊位）的名字就是它；根目录下的文件、根目录本身、
    /// 以及扫描里没有的文件夹（刚建的、被跳过的构建目录）都算广场。返回空串表示广场。
    /// </summary>
    public static string SiteOf(string? relativePath, IReadOnlySet<string> buildingKeys)
    {
        ArgumentNullException.ThrowIfNull(buildingKeys);
        if (string.IsNullOrEmpty(relativePath)) return string.Empty;
        var slash = relativePath.IndexOf('/');
        var first = slash < 0 ? relativePath : relativePath[..slash];
        return buildingKeys.Contains(first) ? first : string.Empty;
    }

    public static PolisFixtureDocument Compose(
        string workspaceName,
        PolisScanResult scan,
        PolisSummary summary,
        PolisLayout layout,
        PolisReplay? replay,
        DateTimeOffset generatedAt,
        bool synthetic)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(layout);

        var byKey = summary.Buildings.ToDictionary(b => b.Key, StringComparer.Ordinal);
        var buildings = layout.Standing
            .Where(e => byKey.ContainsKey(e.Key))
            .Select(e =>
            {
                var b = byKey[e.Key];
                return new PolisFixtureBuilding(
                    b.Key, b.Kind, b.FileCount, b.TotalBytes,
                    b.KindCounts.Where(kv => kv.Value > 0).ToDictionary(kv => JsonNamingPolicy.CamelCase.ConvertName(kv.Key.ToString()), kv => kv.Value),
                    b.LastModifiedUtc, b.State, b.SizeClass, b.Incomplete,
                    new PolisPlot(e.X, e.Z), e.Order);
            })
            .ToList();

        var keys = new HashSet<string>(buildings.Select(b => b.Key).Concat(layout.MarketStalls), StringComparer.Ordinal);
        var annotated = replay is null ? null : replay with
        {
            Events = replay.Events
                .Select(e => e.Place == "inside" ? e with { Building = SiteOf(e.Path, keys) } : e)
                .ToList()
        };

        return new PolisFixtureDocument(
            PolisFixtureDocument.CurrentSchema,
            PolisFixtureDocument.FixtureKind,
            synthetic,
            generatedAt,
            workspaceName,
            new PolisFixtureScan(scan.Files.Count, scan.VisitedEntries, scan.Truncated, scan.SkippedDirectories, scan.UnreadableDirectories.Count),
            new PolisFixtureAgora(summary.Agora.FileCount, summary.Agora.TotalBytes, summary.Agora.LastModifiedUtc, summary.Agora.Incomplete),
            buildings,
            layout.Vacant.Select(v => new PolisFixtureVacantPlot(v.Key, new PolisPlot(v.X, v.Z))).ToList(),
            layout.MarketStalls,
            PolisLayoutLedger.PublicSites.ToDictionary(kv => JsonNamingPolicy.CamelCase.ConvertName(kv.Key.ToString()), kv => kv.Value),
            PolisLayoutLedger.SeaStartsAtZ,
            annotated);
    }

    /// <summary>换行固定为 \n、末尾带一个换行：生成结果与提交进仓库的文件逐字节可比。</summary>
    public static string ToJson(PolisFixtureDocument document)
        => JsonSerializer.Serialize(document, JsonOptions).Replace("\r\n", "\n") + "\n";

    public static string LedgerToJson(PolisLedger ledger)
        => JsonSerializer.Serialize(ledger, JsonOptions).Replace("\r\n", "\n") + "\n";

    public static PolisLedger LedgerFromJson(string json)
        => JsonSerializer.Deserialize<PolisLedger>(json, JsonOptions) ?? PolisLedger.Empty;
}
