using System.Collections.Generic;

namespace Athena.UI.Models;

/// <summary>
/// 知识库搜索结果（语义检索，已按文件聚合）
/// </summary>
public class KnowledgeSearchResult
{
    public string FilePath { get; set; } = string.Empty;
    public string Snippet { get; set; } = string.Empty;
    public double RelevanceScore { get; set; }

    /// <summary>hybrid 表示 RelevanceScore 为余弦相似度；keyword 表示仅按词面排名，不提供置信度。</summary>
    public string RetrievalMode { get; set; } = "keyword";

    /// <summary>命中分块所属的标题路径面包屑（如「文档标题 &gt; 二级标题」），便于模型精确定位后修改。</summary>
    public string HeadingPath { get; set; } = string.Empty;

    /// <summary>该文件内命中的分块数量（&gt;1 表示同一文件多处相关，强烈提示该主题已归属此文件）。</summary>
    public int MatchCount { get; set; } = 1;
}

/// <summary>
/// 知识库中与给定内容语义相近的文件（写入查重 / 去重聚类使用）。
/// </summary>
public class SimilarKnowledgeFile
{
    public string FilePath { get; set; } = string.Empty;
    public double Similarity { get; set; }
    public string Snippet { get; set; } = string.Empty;
}

/// <summary>
/// 一组疑似重复的知识文件（离线向量聚类的产物，供整理 Agent 逐组合并）。
/// </summary>
public class DuplicateFileCluster
{
    public List<string> FilePaths { get; set; } = new();

    /// <summary>组内文件质心之间的最低相似度（越高越像重复）。</summary>
    public double MinSimilarity { get; set; }
}

/// <summary>
/// SEARCH/REPLACE 块的命中层级（由严到松），用于日志与可观测性。
/// </summary>
public enum DiffMatchTier
{
    /// <summary>未命中</summary>
    None = 0,
    /// <summary>逐字符精确匹配</summary>
    Exact,
    /// <summary>容忍行尾空格与换行符差异后匹配</summary>
    TrailingWhitespace,
    /// <summary>容忍前导缩进差异（整行 Trim）后匹配，命中后会对 REPLACE 重缩进</summary>
    Trimmed
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

    /// <summary>
    /// 多块编辑时失败块的序号（1-based）；成功时为 null。
    /// </summary>
    public int? FailedBlockIndex { get; set; }

    /// <summary>
    /// 本次编辑达成的最高命中层级。
    /// </summary>
    public DiffMatchTier MatchTier { get; set; }

    /// <summary>
    /// 未命中时给出的最接近位置提示，便于模型修正下一次尝试。
    /// </summary>
    public string? NearestHint { get; set; }

    /// <summary>
    /// 成功应用后每个修改块落点区域的预览（带行号与上下文），供模型自检编辑位置与结果。
    /// </summary>
    public string? RegionPreview { get; set; }
}
