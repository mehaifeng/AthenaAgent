using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.Services.GameMode;

/// <summary>
/// 城里的十位职业居民（设计稿 7.2；2026-10-13 起不再和工具挂钩，用户：撤下工具能力，留下职业和他管的那摊事，各有性子，能像居民一样聊天）。
/// 每座城一开始就都在；黄金侍女（派发子代理的化身）随之拿掉了。下面每一项的注释是他管的那摊事，也是这项能力原来的归属——
/// <see cref="PolisResidents.Derive"/> 仍按它回答"这项能力用过没有"（委托板的一条建议要看记忆用过没有），但不再决定谁住在城里。
/// </summary>
public enum PolisResidentKind
{
    /// <summary>测绘员：读文件、列目录、搜索。</summary>
    Surveyor,
    /// <summary>抄写员：写文件、改文件、生成文档与演示文稿。</summary>
    Scribe,
    /// <summary>账房：表格。</summary>
    Accountant,
    /// <summary>铁匠：终端命令（锻炉）。</summary>
    Smith,
    /// <summary>水手：网页搜索、抓取、浏览器任务（港口）。</summary>
    Sailor,
    /// <summary>图书管理员：记忆（知识库）。</summary>
    Librarian,
    /// <summary>传令官：定时任务及其结果。</summary>
    Herald,
    /// <summary>画师：生成图片。</summary>
    Painter,
    /// <summary>工匠：技能、外部工具（MCP）、应用设置。</summary>
    Artisan,
    /// <summary>女祭司：每日神谕（神庙）。她不由工具调用唤出，而由这座城设立了神谕唤出。</summary>
    Priestess
}

/// <summary>一位已经出现的居民：什么时候、在哪份委托里第一次用到了他代表的能力，一共用过几次。</summary>
public sealed record PolisResident(PolisResidentKind Kind, DateTimeOffset FirstUsedAt, string? ConversationId, int Uses);

/// <summary>
/// 居民的推导（7.2、10.1"已结识的居民可以从会话归档重新推导"）：扫一遍这座城的委托里成功的工具调用，
/// 每项能力取第一次成功的时刻。失败、被拒绝的调用不算"用到了"。没有存档也推得出来；存档只记你和谁说过话。
/// </summary>
public static class PolisResidents
{
    private static readonly Dictionary<string, PolisResidentKind> ByTool = Build();

    private static Dictionary<string, PolisResidentKind> Build()
    {
        var map = new Dictionary<string, PolisResidentKind>(StringComparer.OrdinalIgnoreCase);
        void Add(PolisResidentKind kind, params string[] tools)
        {
            foreach (var tool in tools) map.Add(tool, kind);
        }

        Add(PolisResidentKind.Surveyor,
            "read_system_file", "list_system_directory", "search_in_file", "search_in_directory", "get_file_info",
            "get_document_outline", "parse_office_document", "inspect_document", "inspect_presentation",
            "validate_document", "validate_presentation");
        Add(PolisResidentKind.Accountant,
            "inspect_spreadsheet", "validate_spreadsheet", "create_spreadsheet", "edit_spreadsheet",
            "modify_spreadsheet_structure", "convert_spreadsheet");
        Add(PolisResidentKind.Scribe,
            "write_system_file", "modify_system_file", "delete_system_file", "create_directory", "move_system_file",
            "copy_system_file", "create_document", "edit_document", "convert_document", "create_presentation", "edit_presentation");
        Add(PolisResidentKind.Smith, "execute_terminal_command");
        Add(PolisResidentKind.Sailor, "web_search", "fetch_url_to_file", "run_browser_task");
        Add(PolisResidentKind.Librarian, "recall_from_memory", "create_new_memory");
        Add(PolisResidentKind.Herald, "create_task", "update_task", "cancel_task", "list_tasks", "run_task_now");
        Add(PolisResidentKind.Painter, "generate_image");
        Add(PolisResidentKind.Artisan,
            "view_self_configuration", "modify_self_configuration", "activate_skill", "read_skill_resource",
            "mcp_list_tools", "mcp_get_tool_schema", "mcp_call_tool", "mcp_add_server", "mcp_remove_server", "mcp_import_json",
            // 黄金侍女拿掉之后（2026-10-13），派发子代理也算"把城外的帮手接进来"
            "dispatch_subagents");
        return map;
    }

    /// <summary>登记过的工具（测试逐个核对 <see cref="PolisToolCategories"/>：新工具忘了给居民会被抓住）。</summary>
    public static IReadOnlyCollection<string> RegisteredTools => ByTool.Keys;

    /// <summary>工具 → 居民；没登记的（MCP 服务器暴露的外部工具名不会走到这里，它们都经 mcp_call_tool）归工匠。</summary>
    public static PolisResidentKind ForTool(string toolName)
        => toolName != null && ByTool.TryGetValue(toolName, out var kind) ? kind : PolisResidentKind.Artisan;

    /// <summary>城里的十位职业居民，按枚举的次序（每座城一开始就都在）。</summary>
    public static IReadOnlyList<PolisResidentKind> All { get; } = Enum.GetValues<PolisResidentKind>();

    /// <summary>她是女子（画师、女祭司；与页面 characters.mjs 的 face.female 一致）。</summary>
    public static bool IsFemale(PolisResidentKind kind) => kind is PolisResidentKind.Painter or PolisResidentKind.Priestess;

    /// <summary>他多大（与页面 characters.mjs 的 face.age 一致）。</summary>
    public static PolisCitizenAge AgeOf(PolisResidentKind kind) => kind switch
    {
        PolisResidentKind.Scribe => PolisCitizenAge.Young,
        PolisResidentKind.Accountant or PolisResidentKind.Librarian => PolisCitizenAge.Old,
        _ => PolisCitizenAge.Adult,
    };

    /// <summary>页面与 locale 里用的名字（camelCase）。</summary>
    public static string Slug(PolisResidentKind kind)
        => char.ToLowerInvariant(kind.ToString()[0]) + kind.ToString()[1..];

    public static bool TryParse(string? slug, out PolisResidentKind kind)
    {
        foreach (var candidate in Enum.GetValues<PolisResidentKind>())
        {
            if (string.Equals(Slug(candidate), slug, StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }
        kind = default;
        return false;
    }

    /// <summary>
    /// 推导这座城的居民。<paramref name="conversations"/> 是属于这座城的委托（会话 Id + 渲染模型里的消息）；
    /// <paramref name="oracleSince"/> 是这座城的每日神谕设立的时刻（没有神谕为 null）——女祭司随神谕出现。
    /// 结果按第一次出现的先后排列，同一时刻按种类的次序。
    /// </summary>
    public static IReadOnlyList<PolisResident> Derive(
        IEnumerable<(string ConversationId, IReadOnlyList<ChatMessage> Messages)> conversations,
        DateTimeOffset? oracleSince = null)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        var first = new Dictionary<PolisResidentKind, (DateTimeOffset At, string? Conversation)>();
        var uses = new Dictionary<PolisResidentKind, int>();
        foreach (var (conversationId, messages) in conversations)
        {
            foreach (var message in messages)
            {
                if (!string.Equals(message.Role, "assistant", StringComparison.Ordinal)) continue;
                var at = new DateTimeOffset(message.Timestamp);
                foreach (var entry in message.Segments.Where(s => s.IsToolCallGroup).SelectMany(s => s.ToolCalls))
                {
                    if (entry.Status != ToolCallStatus.Success || string.IsNullOrEmpty(entry.Name)) continue;
                    var kind = ForTool(entry.Name);
                    uses[kind] = uses.GetValueOrDefault(kind) + 1;
                    if (!first.TryGetValue(kind, out var known) || at < known.At
                        || (at == known.At && string.CompareOrdinal(conversationId, known.Conversation) < 0))
                    {
                        first[kind] = (at, conversationId);
                    }
                }
            }
        }
        if (oracleSince is { } since && !first.ContainsKey(PolisResidentKind.Priestess))
        {
            first[PolisResidentKind.Priestess] = (since, null);
            uses[PolisResidentKind.Priestess] = 1;
        }
        return first
            .Select(kv => new PolisResident(kv.Key, kv.Value.At, kv.Value.Conversation, uses.GetValueOrDefault(kv.Key)))
            .OrderBy(r => r.FirstUsedAt)
            .ThenBy(r => r.Kind)
            .ToList();
    }
}
