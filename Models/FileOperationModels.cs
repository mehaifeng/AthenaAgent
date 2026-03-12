using System.Collections.Generic;

namespace Athena.UI.Models;

/// <summary>
/// 知识库搜索结果（语义检索）
/// </summary>
public class KnowledgeSearchResult
{
    public string FilePath { get; set; } = string.Empty;
    public string Snippet { get; set; } = string.Empty;
    public double RelevanceScore { get; set; }
}

/// <summary>
/// 文件更新/修改操作的结果（通用）
/// </summary>
public class FileUpdateResult
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 结果消息
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// 修改后的完整内容（内部使用）
    /// </summary>
    internal string? ModifiedContent { get; set; }

    /// <summary>
    /// 匹配到的起始行号
    /// </summary>
    public int? LineNumber { get; set; }

    /// <summary>
    /// 多处匹配时的上下文预览列表
    /// </summary>
    public List<string>? MultipleMatches { get; set; }

    /// <summary>
    /// 成功应用的修改块（SEARCH/REPLACE 块）数量
    /// </summary>
    public int AppliedBlocks { get; set; }
}
