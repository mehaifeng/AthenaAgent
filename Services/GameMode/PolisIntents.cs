using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Athena.UI.Services.GameMode;

/// <summary>
/// 网页能请求 C# 做的事——一个封闭集合（设计稿 12.4）。这里没有、也永远不会有"批准"：
/// 页面里显示的文字有一部分是模型写的，一旦被注入脚本，就能替你点同意。审批只在原生审批窗口里。
/// 2026-10-11 起游戏盖住了整个中间区域（输入框也在其中），城里的对话栏可以把你写的字托付出去（<see cref="Say"/>）：
/// 只送到此刻显示的这一份委托，走输入框同一条发送路径；工具调用照旧过审批闸门。
/// </summary>
public enum PolisIntentType
{
    /// <summary>页面自检通过、可以接收城邦了。</summary>
    Ready,
    /// <summary>页面自检失败（WebGL 不可用、脚本出错），附原因与技术细节。</summary>
    Failed,
    /// <summary>选中一个会话（点橄榄树、点雅典娜……）——走会话树的同一条路径。</summary>
    SelectConversation,
    /// <summary>在这座城邦里开一份新委托（新建会话）。</summary>
    NewCommission,
    /// <summary>在工作台里打开一个文件。</summary>
    OpenFile,
    /// <summary>把一个文件作为附件放进输入框。</summary>
    AttachFile,
    /// <summary>预填输入框（不发送；发不发由你决定）。</summary>
    PrefillInput,
    /// <summary>停止当前回合。</summary>
    StopTurn,
    /// <summary>收下一件成果。</summary>
    AcceptDelivery,
    /// <summary>退回一件成果：在同一会话里追加一条修改要求（预填输入框并聚焦，由你写完发出）。</summary>
    ReturnDelivery,
    /// <summary>在卷轴阅读器里读一件成果（回答的正文按需发给页面）。</summary>
    ReadScroll,
    /// <summary>文件夹不见了，重新定位它（打开原生的文件夹选择器）。</summary>
    Relocate,
    /// <summary>记下玩家的位置与镜头缩放（存档里的"玩家进度"）。</summary>
    SavePlayer,
    /// <summary>把一件收下的藏品摆进一处场馆的某个位次（或挪过去）。只改存档，不碰工作区（7.2）。</summary>
    PlaceItem,
    /// <summary>把一件藏品撤下来：回到"收着、没摆出来"。</summary>
    UnplaceItem,
    /// <summary>移除一件遗失藏品的记录——只有你点了才会删（11.3）。</summary>
    RemoveItem,
    /// <summary>采纳委托板上的一条建议：新建一份委托并预填请求（不发送）。</summary>
    TakeSuggestion,
    /// <summary>在神庙请雅典娜设立每日神谕：新建一份委托并预填请求（不发送；建任务走现有审批）。</summary>
    RequestOracle,
    /// <summary>在海图上选一座岛：选中那个工作区最近的会话（没有就新建）——走会话树的同一条路径。</summary>
    SelectIsland,
    /// <summary>和一位居民说过话（记进玩家进度"已结识的居民"）。</summary>
    MeetResident,
    /// <summary>请雅典娜试试这位居民代表的能力：新建一份委托并预填一句示范请求（不发送）。</summary>
    TryResident,
    /// <summary>
    /// 城里对话栏的"托付"：把你写的这段话发给此刻显示的这份委托——走输入框的同一条路径（SendMessageCommand）。
    /// 只认显示着的那一份（ViewModel 核对），不能替别的会话说话；她在回答时不收。
    /// </summary>
    Say,
    /// <summary>对话栏的草稿：同步进这份委托自己的输入框（切回对话模式，写了一半的话还在）。不发送。</summary>
    Draft,
    /// <summary>
    /// 和城里的一个人说一句话（2026-10-13，原来 M-C 的"问他那件事"）：<c>who</c> 只认这座城里此刻有的居民，一句话去掉首尾空白后
    /// 1–<see cref="PolisTalk.MaxLineChars"/> 字，超长整条拒绝。不替你说任何话：他的回答只回到页面（他头顶的气泡），不进任何会话。
    /// </summary>
    Chat,
    /// <summary>
    /// 两位居民在街上碰上了，请他们聊两句（2026-10-13，用户：NPC 之间的对话只用模型，不预置台词）。页面挑人、挑时机；
    /// <c>a</c>、<c>b</c> 都得是这座城里此刻有的居民，而且不是同一位。他们的话只回到页面（头顶的气泡），不进任何会话。
    /// </summary>
    AmbientChat,
    /// <summary>
    /// 看看一位居民那场会话（2026-10-13）：只认这座城里此刻有的居民。C# 回 <c>citizen-transcript</c>——你和雅典娜看得见的那些话，
    /// 工具、推理一概不带；只回到页面的卷轴阅读器（模型写的字只经 textContent 进 DOM）。
    /// </summary>
    CitizenTranscript,
    /// <summary>在讲台前提一个议题，开一场辩论（M-E）：议题去掉首尾空白后 1–200 字，超长整条拒绝。</summary>
    DebateStart,
    /// <summary>你这一轮的论点（1–600 字，超长整条拒绝）。只在等你说的时候收（ViewModel 核对状态）。</summary>
    DebateSay,
    /// <summary>不辩了 / 辩完离开：这场作废（没判完的不记战绩）。</summary>
    DebateEnd
}

/// <summary>校验通过的一条意图。路径已换算成磁盘上的绝对路径（<see cref="FullPath"/>）。</summary>
public sealed record PolisIntent(PolisIntentType Type)
{
    public string? ConversationId { get; init; }
    public string? RelativePath { get; init; }
    public string? FullPath { get; init; }
    public string? Text { get; init; }
    public string? ItemId { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }
    public double? X { get; init; }
    public double? Z { get; init; }
    public double? Zoom { get; init; }
    public PolisVenue? Venue { get; init; }
    public int? Slot { get; init; }
    public string? SuggestionId { get; init; }
    public string? IslandId { get; init; }
    public PolisResidentKind? Resident { get; init; }
    /// <summary>两个人的那一种意图里的另一位（<see cref="PolisIntentType.AmbientChat"/> 的 b）。</summary>
    public string? OtherId { get; init; }
}

/// <summary>校验时需要知道的现场：当前城邦的根目录（神殿为 null）、有哪些会话、有哪些藏品。</summary>
public sealed record PolisIntentContext(
    string? WorkspaceRoot,
    IReadOnlySet<string> KnownConversationIds,
    IReadOnlySet<string> KnownItemIds)
{
    /// <summary>委托板上此刻挂着的建议（页面只能采纳 C# 生成的那几条，不能自己写一条）。</summary>
    public IReadOnlySet<string> KnownSuggestionIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>海图上的岛：工作区 Id 与神殿。</summary>
    public IReadOnlySet<string> KnownIslandIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>这座城里此刻出现了的居民（camelCase 名）。</summary>
    public IReadOnlySet<string> KnownResidentIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>这座城的居民（M-B：每场会话一位，会话 Id）——问话只问得到这座城里的人。</summary>
    public IReadOnlySet<string> KnownCitizenIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

public sealed record PolisIntentResult(PolisIntent? Intent, string? Rejection)
{
    public bool Accepted => Intent != null;

    public static PolisIntentResult Reject(string reason) => new(null, reason);
}

/// <summary>
/// 网页 → C# 的每一条消息都在这里逐条校验（12.3 / 12.4）。页面是不可信的：它画的文字一部分来自模型，
/// 脚本万一被注入，这里就是最后一道关。规则：只认封闭集合里的类型；字段逐个按类型取；路径必须是干净的
/// 相对路径，字面上和解析软链之后都落在当前工作区里；会话与藏品必须真实存在；文字有长度上限、去掉控制字符。
/// </summary>
public static class PolisIntents
{
    /// <summary>一条消息的上限：一次托付最多 <see cref="MaxSayChars"/> 个字，JSON 转义（换行、引号）之后要放得下。</summary>
    public const int MaxMessageChars = 64 * 1024;
    public const int MaxPrefillChars = 2000;
    /// <summary>对话栏一次托付 / 一份草稿最多多少字（页面的输入框同一个上限，超过的打不进去）。</summary>
    public const int MaxSayChars = 16000;
    public const int MaxDetailChars = 4000;
    public const int ProtocolVersion = 1;

    /// <summary>看起来像"审批"的类型名：单独点名拒绝，日志里一眼能认出这是一次越权尝试。</summary>
    private static readonly HashSet<string> ApprovalLikeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "approve", "approval", "approve-tool", "allow", "allow-always", "grant", "deny", "decide-approval", "consent"
    };

    private static readonly Dictionary<string, PolisIntentType> TypeNames = new(StringComparer.Ordinal)
    {
        ["ready"] = PolisIntentType.Ready,
        ["failed"] = PolisIntentType.Failed,
        ["select-conversation"] = PolisIntentType.SelectConversation,
        ["new-commission"] = PolisIntentType.NewCommission,
        ["open-file"] = PolisIntentType.OpenFile,
        ["attach-file"] = PolisIntentType.AttachFile,
        ["prefill-input"] = PolisIntentType.PrefillInput,
        ["stop-turn"] = PolisIntentType.StopTurn,
        ["accept-delivery"] = PolisIntentType.AcceptDelivery,
        ["return-delivery"] = PolisIntentType.ReturnDelivery,
        ["read-scroll"] = PolisIntentType.ReadScroll,
        ["relocate"] = PolisIntentType.Relocate,
        ["save-player"] = PolisIntentType.SavePlayer,
        ["place-item"] = PolisIntentType.PlaceItem,
        ["unplace-item"] = PolisIntentType.UnplaceItem,
        ["remove-item"] = PolisIntentType.RemoveItem,
        ["take-suggestion"] = PolisIntentType.TakeSuggestion,
        ["request-oracle"] = PolisIntentType.RequestOracle,
        ["select-island"] = PolisIntentType.SelectIsland,
        ["meet-resident"] = PolisIntentType.MeetResident,
        ["try-resident"] = PolisIntentType.TryResident,
        ["say"] = PolisIntentType.Say,
        ["draft"] = PolisIntentType.Draft,
        ["chat"] = PolisIntentType.Chat,
        ["ambient-chat"] = PolisIntentType.AmbientChat,
        ["citizen-transcript"] = PolisIntentType.CitizenTranscript,
        ["debate-start"] = PolisIntentType.DebateStart,
        ["debate-say"] = PolisIntentType.DebateSay,
        ["debate-end"] = PolisIntentType.DebateEnd,
    };

    /// <summary>页面那一侧用的名字（测试与文档用）。</summary>
    public static IReadOnlyCollection<string> KnownTypeNames => TypeNames.Keys;

    public static PolisIntentResult Validate(string? message, PolisIntentContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrEmpty(message)) return PolisIntentResult.Reject("empty message");
        if (message.Length > MaxMessageChars) return PolisIntentResult.Reject($"message longer than {MaxMessageChars} characters");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(message);
        }
        catch (JsonException)
        {
            return PolisIntentResult.Reject("not JSON");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return PolisIntentResult.Reject("not a JSON object");
            if (!root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var v) || v != ProtocolVersion)
                return PolisIntentResult.Reject("unknown protocol version");
            if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                return PolisIntentResult.Reject("missing type");
            var typeName = typeElement.GetString() ?? string.Empty;
            if (ApprovalLikeTypes.Contains(typeName))
                return PolisIntentResult.Reject($"'{typeName}' is not a page intent: approvals are only decided in the native approval window");
            if (!TypeNames.TryGetValue(typeName, out var type))
                return PolisIntentResult.Reject($"unknown intent type '{Truncate(typeName, 40)}'");

            switch (type)
            {
                case PolisIntentType.Ready:
                case PolisIntentType.NewCommission:
                case PolisIntentType.StopTurn:
                case PolisIntentType.Relocate:
                case PolisIntentType.RequestOracle:
                case PolisIntentType.DebateEnd:
                    return new PolisIntentResult(new PolisIntent(type), null);

                case PolisIntentType.DebateStart:
                case PolisIntentType.DebateSay:
                {
                    var field = type == PolisIntentType.DebateStart ? "motion" : "text";
                    var max = type == PolisIntentType.DebateStart ? PolisDebate.MaxMotionChars : PolisDebate.MaxArgumentChars;
                    var raw = ReadString(root, field);
                    if (raw == null) return PolisIntentResult.Reject($"missing {field}");
                    var text = Clean(raw.Trim(), int.MaxValue)!;
                    if (text.Length == 0) return PolisIntentResult.Reject($"empty {field}");
                    // 和托付、问话一样不截断：截掉半句，议题或论点就变了
                    if (text.Length > max) return PolisIntentResult.Reject($"{field} longer than {max} characters");
                    return new PolisIntentResult(new PolisIntent(type) { Text = text }, null);
                }

                case PolisIntentType.Failed:
                    return new PolisIntentResult(new PolisIntent(type)
                    {
                        Reason = Clean(ReadString(root, "reason"), 64) ?? "unknown",
                        Detail = Clean(ReadString(root, "detail"), MaxDetailChars)
                    }, null);

                case PolisIntentType.SelectConversation:
                {
                    var id = ReadString(root, "conversationId");
                    if (string.IsNullOrEmpty(id) || !context.KnownConversationIds.Contains(id))
                        return PolisIntentResult.Reject("unknown conversation");
                    return new PolisIntentResult(new PolisIntent(type) { ConversationId = id }, null);
                }

                case PolisIntentType.OpenFile:
                case PolisIntentType.AttachFile:
                {
                    var relative = ReadString(root, "path");
                    var resolved = ResolveInside(context.WorkspaceRoot, relative);
                    return resolved.Rejection != null
                        ? PolisIntentResult.Reject(resolved.Rejection)
                        : new PolisIntentResult(new PolisIntent(type) { RelativePath = relative, FullPath = resolved.FullPath }, null);
                }

                case PolisIntentType.PrefillInput:
                {
                    var text = Clean(ReadString(root, "text"), MaxPrefillChars);
                    if (string.IsNullOrWhiteSpace(text)) return PolisIntentResult.Reject("empty text");
                    return new PolisIntentResult(new PolisIntent(type) { Text = text }, null);
                }

                case PolisIntentType.Say:
                case PolisIntentType.Draft:
                {
                    var id = ReadString(root, "conversationId");
                    if (string.IsNullOrEmpty(id) || !context.KnownConversationIds.Contains(id))
                        return PolisIntentResult.Reject("unknown conversation");
                    var raw = ReadString(root, "text");
                    if (raw == null) return PolisIntentResult.Reject("missing text");
                    // 托付的字不截断：截掉一半再发出去，等于替你改了要求——超长的整条拒绝（页面的输入框本来就打不进去）
                    if (raw.Length > MaxSayChars) return PolisIntentResult.Reject($"text longer than {MaxSayChars} characters");
                    var text = Clean(raw, MaxSayChars)!;
                    if (type == PolisIntentType.Say && string.IsNullOrWhiteSpace(text)) return PolisIntentResult.Reject("empty text");
                    return new PolisIntentResult(new PolisIntent(type) { ConversationId = id, Text = text }, null);
                }

                case PolisIntentType.Chat:
                {
                    var who = ReadString(root, "who");
                    if (string.IsNullOrEmpty(who) || !context.KnownCitizenIds.Contains(who))
                        return PolisIntentResult.Reject("unknown speaker");
                    var raw = ReadString(root, "text");
                    if (raw == null) return PolisIntentResult.Reject("missing text");
                    var line = Clean(raw.Trim(), int.MaxValue)!;
                    if (line.Length == 0) return PolisIntentResult.Reject("empty text");
                    // 一句话也不截断：截掉半句再说，说的就是另一回事了（页面的输入框本来就打不进更多）
                    if (line.Length > PolisTalk.MaxLineChars)
                        return PolisIntentResult.Reject($"text longer than {PolisTalk.MaxLineChars} characters");
                    return new PolisIntentResult(new PolisIntent(type) { ConversationId = who, Text = line }, null);
                }

                case PolisIntentType.AmbientChat:
                {
                    var a = ReadString(root, "a");
                    var b = ReadString(root, "b");
                    if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || !context.KnownCitizenIds.Contains(a) || !context.KnownCitizenIds.Contains(b))
                        return PolisIntentResult.Reject("unknown citizen");
                    if (string.Equals(a, b, StringComparison.Ordinal)) return PolisIntentResult.Reject("a citizen cannot chat with himself");
                    return new PolisIntentResult(new PolisIntent(type) { ConversationId = a, OtherId = b }, null);
                }

                case PolisIntentType.CitizenTranscript:
                {
                    var id = ReadString(root, "conversationId");
                    if (string.IsNullOrEmpty(id) || !context.KnownCitizenIds.Contains(id))
                        return PolisIntentResult.Reject("unknown citizen");
                    return new PolisIntentResult(new PolisIntent(type) { ConversationId = id }, null);
                }

                case PolisIntentType.PlaceItem:
                {
                    var itemId = ReadString(root, "itemId");
                    if (string.IsNullOrEmpty(itemId) || !context.KnownItemIds.Contains(itemId))
                        return PolisIntentResult.Reject("unknown item");
                    if (!PolisCollection.TryParseVenue(ReadString(root, "venue"), out var venue))
                        return PolisIntentResult.Reject("unknown venue");
                    int? slot = null;
                    if (root.TryGetProperty("slot", out var rawSlot) && rawSlot.ValueKind != JsonValueKind.Null)
                    {
                        if (rawSlot.ValueKind != JsonValueKind.Number || !rawSlot.TryGetInt32(out var s)
                            || s < 0 || s >= PolisCollection.SlotsPerVenue)
                            return PolisIntentResult.Reject("slot out of range");
                        slot = s;
                    }
                    return new PolisIntentResult(new PolisIntent(type) { ItemId = itemId, Venue = venue, Slot = slot }, null);
                }

                case PolisIntentType.TakeSuggestion:
                {
                    var id = ReadString(root, "suggestionId");
                    if (string.IsNullOrEmpty(id) || !context.KnownSuggestionIds.Contains(id))
                        return PolisIntentResult.Reject("unknown suggestion");
                    return new PolisIntentResult(new PolisIntent(type) { SuggestionId = id }, null);
                }

                case PolisIntentType.SelectIsland:
                {
                    var id = ReadString(root, "islandId");
                    if (string.IsNullOrEmpty(id) || !context.KnownIslandIds.Contains(id))
                        return PolisIntentResult.Reject("unknown island");
                    return new PolisIntentResult(new PolisIntent(type) { IslandId = id }, null);
                }

                case PolisIntentType.MeetResident:
                case PolisIntentType.TryResident:
                {
                    var id = ReadString(root, "residentId");
                    if (string.IsNullOrEmpty(id) || !context.KnownResidentIds.Contains(id) || !PolisResidents.TryParse(id, out var kind))
                        return PolisIntentResult.Reject("unknown resident");
                    return new PolisIntentResult(new PolisIntent(type) { Resident = kind }, null);
                }

                case PolisIntentType.AcceptDelivery:
                case PolisIntentType.ReturnDelivery:
                case PolisIntentType.ReadScroll:
                case PolisIntentType.UnplaceItem:
                case PolisIntentType.RemoveItem:
                {
                    var itemId = ReadString(root, "itemId");
                    if (string.IsNullOrEmpty(itemId) || !context.KnownItemIds.Contains(itemId))
                        return PolisIntentResult.Reject("unknown item");
                    return new PolisIntentResult(new PolisIntent(type) { ItemId = itemId }, null);
                }

                case PolisIntentType.SavePlayer:
                {
                    var x = ReadNumber(root, "x");
                    var z = ReadNumber(root, "z");
                    var zoom = ReadNumber(root, "zoom");
                    if (x is not { } px || z is not { } pz || Math.Abs(px) > 10_000 || Math.Abs(pz) > 10_000)
                        return PolisIntentResult.Reject("player position out of range");
                    if (zoom is { } pzoom && (pzoom < 0.1 || pzoom > 10)) zoom = null;
                    return new PolisIntentResult(new PolisIntent(type) { X = px, Z = pz, Zoom = zoom }, null);
                }

                default:
                    return PolisIntentResult.Reject("unhandled intent");
            }
        }
    }

    /// <summary>
    /// 相对路径 → 工作区里的绝对路径。三道关：写法必须干净（<see cref="PolisPaths.IsCleanRelativePath"/>）；
    /// 字面上落在根之内；解析软链之后（与文件工具同一个 realpath 实现）真实位置也在根之内——
    /// 工作区里一个指向 ~/.ssh 的软链，不能让页面借"打开文件"读到外面去。神殿（没有工作区）一律拒绝。
    /// </summary>
    public static (string? FullPath, string? Rejection) ResolveInside(string? workspaceRoot, string? relativePath)
    {
        if (string.IsNullOrEmpty(workspaceRoot)) return (null, "no workspace: the sanctuary has no files");
        if (!PolisPaths.IsCleanRelativePath(relativePath)) return (null, "path is not a clean workspace-relative path");
        var comparison = PolisPaths.PlatformComparison;
        var full = PolisPaths.ToFull(workspaceRoot, relativePath!, comparison);
        if (full == null) return (null, "path leaves the workspace");

        var rootResolved = FileSystemService.TryResolveFully(Path.GetFullPath(workspaceRoot), out var realRoot);
        var resolved = FileSystemService.TryResolveFully(full, out var realPath);
        if (!rootResolved || !resolved) return (null, "path could not be resolved");
        if (PolisPaths.ToRelative(realRoot, realPath, comparison) is not { Length: > 0 })
            return (null, "path resolves through a link to a place outside the workspace");
        return (full, null);
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? ReadNumber(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) && double.IsFinite(d)
            ? d
            : null;

    /// <summary>截断并去掉控制字符（保留换行与制表）。</summary>
    private static string? Clean(string? text, int max)
    {
        if (text == null) return null;
        var builder = new StringBuilder(Math.Min(text.Length, max));
        foreach (var ch in text)
        {
            if (builder.Length >= max) break;
            if (char.IsControl(ch) && ch is not '\n' and not '\t') continue;
            builder.Append(ch);
        }
        return builder.ToString();
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
