using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.Services;

/// <summary>
/// 判断一次对话是否需要 Office（OOXML）工具集。
///
/// 为什么是「判断」而不是「让模型自己解锁」：工具列表在
/// <c>CreateRequestRuntimeSnapshotAsync</c> 里随请求快照一次性绑定，整个用户回合内
/// 不再重建。模型在回合中途调用一个「解锁工具」的工具，标志位改了也没人再读——
/// 它会被告知工具已可用，却永远看不到它，然后一遍遍去调一个不存在的名字。
/// 这条路在本架构下不成立，所以改成在「工具列表真正被构建的那一刻」用对话本身来判断。
///
/// 判据故意从宽：判错了只是多带一份声明（几千 token），判漏了却会让模型答不出
/// 「帮我做份 PPT」。因此只要整段对话里出现过任何 Office 意图，就一直带上。
/// </summary>
public static class OfficeToolRelevance
{
    /// <summary>扩展名信号：出现即认定，无论中英文语境。</summary>
    private static readonly string[] Extensions =
    {
        ".xlsx", ".xlsm", ".xltx", ".docx", ".docm", ".dotx", ".pptx", ".pptm", ".potx", ".potm"
    };

    /// <summary>词面信号。中英文都列，因为两种说法在本项目里都很常见。</summary>
    private static readonly string[] Keywords =
    {
        // 演示
        "ppt", "powerpoint", "presentation", "slide", "deck", "keynote",
        "幻灯", "演示文稿", "演示稿", "投影片",
        // 表格
        "excel", "spreadsheet", "workbook", "worksheet", "csv",
        "表格", "工作簿", "工作表", "报表",
        // 文档
        "word", "docx", "文档", "文稿", "报告", "简历", "公文",
        // 通用
        "office"
    };

    /// <summary>
    /// 对话里是否出现过 Office 意图。传入整段对话的文本（用户消息即可，
    /// 工具结果不必参与——它们会把无关的路径字符串也带进来，徒增误判）。
    /// </summary>
    public static bool IsRelevant(IEnumerable<string?> conversationText)
    {
        if (conversationText is null) return false;

        foreach (var text in conversationText)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var lowered = text.ToLowerInvariant();

            foreach (var extension in Extensions)
            {
                if (lowered.Contains(extension, StringComparison.Ordinal)) return true;
            }

            foreach (var keyword in Keywords)
            {
                if (lowered.Contains(keyword, StringComparison.Ordinal)) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 从一段会话上下文判定。工具结果被排除在外：它们会把无关的路径字符串带进来，徒增误判。
    /// </summary>
    public static bool IsRelevant(Models.ConversationContext? context)
    {
        if (context is null) return false;
        return IsRelevant(context.Messages
            .Where(message => !string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
            .Select(message => message.Content));
    }
}

/// <summary>受按需披露管辖的 Office 工具名，单一真源。</summary>
public static class OfficeToolNames
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "inspect_spreadsheet", "create_spreadsheet", "edit_spreadsheet",
        "modify_spreadsheet_structure", "convert_spreadsheet", "validate_spreadsheet",
        "inspect_document", "create_document", "edit_document",
        "convert_document", "validate_document",
        "inspect_presentation", "create_presentation", "edit_presentation", "validate_presentation"
    };
}
