using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Athena.UI.Services.GameMode;

/// <summary>
/// 网页能请求 C# 做的事——一个封闭集合（设计稿 12.4）。这里没有、也永远不会有"批准"：
/// 页面里显示的文字有一部分是模型写的，一旦被注入脚本，就能替你点同意。审批只在原生审批窗口里。
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
    SavePlayer
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
}

/// <summary>校验时需要知道的现场：当前城邦的根目录（神殿为 null）、有哪些会话、有哪些藏品。</summary>
public sealed record PolisIntentContext(
    string? WorkspaceRoot,
    IReadOnlySet<string> KnownConversationIds,
    IReadOnlySet<string> KnownItemIds);

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
    public const int MaxMessageChars = 16 * 1024;
    public const int MaxPrefillChars = 2000;
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
                    return new PolisIntentResult(new PolisIntent(type), null);

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

                case PolisIntentType.AcceptDelivery:
                case PolisIntentType.ReturnDelivery:
                case PolisIntentType.ReadScroll:
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
