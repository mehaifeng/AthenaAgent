using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.Services.GameMode;

/// <summary>委托板上的一种建议。每一种在 locale 里有一句板上的话（<c>Polis.Board.&lt;Kind&gt;</c>）和一段预填的请求（<c>…Prompt</c>）。</summary>
public enum PolisSuggestionKind
{
    /// <summary>上次停在半路的委托：接着做。</summary>
    ResumeCommission,
    /// <summary>一件藏品找不到了：找找它去了哪儿。</summary>
    FindLostItem,
    /// <summary>根目录（广场）上散着很多文件：归类。</summary>
    TidyAgora,
    /// <summary>一座民居（什么都有的文件夹）很大：分分类。</summary>
    SortMixedBuilding,
    /// <summary>很久没动的文件夹：清点还用得着什么。</summary>
    ReviewStaleBuilding,
    /// <summary>一座雕塑园（图片）：做个目录。</summary>
    CatalogImages,
    /// <summary>一座金库（表格）：汇总。</summary>
    SummarizeSheets,
    /// <summary>一座柱廊书库（文档）：各写一句摘要。</summary>
    SummarizeDocuments,
    /// <summary>一座作坊（代码）：写份说明。</summary>
    DescribeWorkshop,
    /// <summary>一座仓库（压缩包）：看看都装了什么。</summary>
    InspectArchives,
    /// <summary>这两天改得最多的文件夹：总结最近的变化。</summary>
    RecapBusyBuilding,
    /// <summary>还没有每日神谕：去神庙设一个。</summary>
    SetOracle,
    /// <summary>一座空城：规划一下。</summary>
    PlanEmptyCity,
    /// <summary>神殿（没有文件夹）：问我任何事。</summary>
    AskAnything,
    /// <summary>还没让雅典娜记过东西：让她记住你的偏好。</summary>
    RememberPreference
}

/// <summary>一条建议：种类 + 主语（文件夹名、委托标题、藏品名）+ 数量。文字由视图模型按 locale 拼出来。</summary>
public sealed record PolisSuggestion(string Id, PolisSuggestionKind Kind, string? Subject, int Count, int AgeDays, string? ConversationId);

/// <summary>生成建议要看的统计：只有名字、数量、时间与状态，没有任何文件内容（7.3"只读统计信息"）。</summary>
public sealed record PolisSuggestionInput(
    string? WorkspaceId,
    IReadOnlyList<PolisFixtureBuilding> Buildings,
    PolisFixtureAgora? Agora,
    IReadOnlyList<PolisItem> Items,
    IReadOnlyList<PolisCommission> Commissions,
    bool HasOracle,
    bool HasMemory,
    DateTimeOffset Now);

/// <summary>
/// 委托板（设计稿 7.3）：根据工作区现状在本地生成三条具体建议，比如"下载区堆了 300 件东西，要不要整理"。
/// 只读统计、不调用模型——解决的是非技术用户"不知道该让它干什么"。纯函数，同一天、同样的统计给出同样的三条；
/// 分数相同的候选按"日期 + 候选"的哈希轮换，所以板子每天会换一换，但一天之内是稳定的。
/// </summary>
public static class PolisSuggestions
{
    public const int Count = 3;

    public static IReadOnlyList<PolisSuggestion> Generate(PolisSuggestionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var candidates = new List<(PolisSuggestion Suggestion, int Score)>();
        void Add(PolisSuggestionKind kind, string? subject, int count, int score, int ageDays = 0, string? conversationId = null)
        {
            var id = Slug(kind) + ":" + (subject ?? string.Empty);
            if (candidates.Any(c => c.Suggestion.Id == id)) return;
            candidates.Add((new PolisSuggestion(id, kind, subject, count, ageDays, conversationId), score));
        }

        // 停在半路的委托最要紧：那是你已经开了头的事
        foreach (var c in input.Commissions.Where(c => c.Interrupted && !c.Running).OrderByDescending(c => c.UpdatedAt).Take(1))
            Add(PolisSuggestionKind.ResumeCommission, c.Title, 0, 100, conversationId: c.ConversationId);

        foreach (var item in input.Items.Where(i => i.Lost && i.State != PolisItemState.Returned).OrderByDescending(i => i.CreatedAt).Take(1))
            Add(PolisSuggestionKind.FindLostItem, item.Title, 0, 80);

        var isSanctuary = input.WorkspaceId == null;
        if (isSanctuary)
        {
            Add(PolisSuggestionKind.AskAnything, null, 0, 40);
        }
        else if (input.Buildings.Count == 0 && (input.Agora?.Files ?? 0) == 0)
        {
            Add(PolisSuggestionKind.PlanEmptyCity, null, 0, 60);
        }

        if (input.Agora is { Files: >= 15 } agora)
            Add(PolisSuggestionKind.TidyAgora, null, agora.Files, 55 + Math.Min(20, agora.Files / 10));

        foreach (var b in input.Buildings)
        {
            var ageDays = Math.Max(0, (int)(input.Now - b.LastModified).TotalDays);
            switch (b.Kind)
            {
                case PolisBuildingKind.House when b.Files >= 40:
                    Add(PolisSuggestionKind.SortMixedBuilding, b.Key, b.Files, 50 + Math.Min(25, b.Files / 20));
                    break;
                case PolisBuildingKind.SculptureGarden when b.Files >= 12:
                    Add(PolisSuggestionKind.CatalogImages, b.Key, b.Files, 35 + Math.Min(15, b.Files / 20));
                    break;
                case PolisBuildingKind.Treasury when b.Files >= 2:
                    Add(PolisSuggestionKind.SummarizeSheets, b.Key, b.Files, 38 + Math.Min(12, b.Files));
                    break;
                case PolisBuildingKind.StoaLibrary when b.Files >= 3:
                    Add(PolisSuggestionKind.SummarizeDocuments, b.Key, b.Files, 36 + Math.Min(12, b.Files / 2));
                    break;
                case PolisBuildingKind.Workshop when b.Files >= 5:
                    Add(PolisSuggestionKind.DescribeWorkshop, b.Key, b.Files, 30 + Math.Min(10, b.Files / 50));
                    break;
                case PolisBuildingKind.Warehouse when b.Files >= 3:
                    Add(PolisSuggestionKind.InspectArchives, b.Key, b.Files, 34 + Math.Min(10, b.Files / 5));
                    break;
            }
            if (b.State is PolisBuildingState.Ivy or PolisBuildingState.Ruin && b.Files >= 5)
                Add(PolisSuggestionKind.ReviewStaleBuilding, b.Key, b.Files, 42 + Math.Min(10, ageDays / 90), ageDays);
        }

        // 这两天改得最多的那座：常用的地方最值得回顾
        var busiest = input.Buildings
            .Where(b => b.State == PolisBuildingState.Bustling && b.Files >= 3)
            .OrderByDescending(b => b.LastModified)
            .ThenBy(b => b.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        if (busiest != null) Add(PolisSuggestionKind.RecapBusyBuilding, busiest.Key, busiest.Files, 45);

        if (!input.HasOracle) Add(PolisSuggestionKind.SetOracle, null, 0, 25);
        if (!input.HasMemory) Add(PolisSuggestionKind.RememberPreference, null, 0, 20);

        var day = input.Now.UtcDateTime.Date.ToString("yyyy-MM-dd");
        var chosen = new List<PolisSuggestion>();
        var kinds = new HashSet<PolisSuggestionKind>();
        foreach (var (suggestion, _) in candidates
                     .OrderByDescending(c => c.Score)
                     .ThenBy(c => Rotate(day, input.WorkspaceId, c.Suggestion.Id))
                     .ThenBy(c => c.Suggestion.Id, StringComparer.Ordinal))
        {
            // 同一种建议板上只出现一次（三条都是"写摘要"就不算三条建议）
            if (!kinds.Add(suggestion.Kind)) continue;
            chosen.Add(suggestion);
            if (chosen.Count == Count) break;
        }
        return chosen;
    }

    public static string Slug(PolisSuggestionKind kind)
        => char.ToLowerInvariant(kind.ToString()[0]) + kind.ToString()[1..];

    /// <summary>同分候选的轮换次序：FNV-1a（与页面的 hashSeed 同一种），不依赖 string.GetHashCode 的进程随机化。</summary>
    private static uint Rotate(string day, string? workspaceId, string id)
    {
        var h = 0x811c9dc5u;
        foreach (var ch in day + "|" + (workspaceId ?? "sanctuary") + "|" + id)
        {
            h ^= ch;
            h *= 0x01000193u;
        }
        return h;
    }
}
