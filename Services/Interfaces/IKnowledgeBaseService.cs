using Athena.UI.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 知识库服务接口
/// 管理本地 Markdown 文件形式的知识库
/// </summary>
public interface IKnowledgeBaseService
{
    /// <summary>
    /// 知识库根目录路径
    /// </summary>
    string KnowledgeBasePath { get; }

    /// <summary>
    /// 创建知识文件
    /// </summary>
    /// <param name="relativePath">相对路径（如 "Characters/user_profile.md"）</param>
    /// <param name="content">Markdown 内容</param>
    /// <param name="tags">标签（可选）</param>
    Task CreateFileAsync(string relativePath, string content, string[]? tags = null);

    /// <summary>
    /// 追加内容到知识文件
    /// </summary>
    /// <param name="relativePath">相对路径</param>
    /// <param name="content">要追加的内容</param>
    Task AppendToFileAsync(string relativePath, string content);

    /// <summary>
    /// 替换知识文件内容
    /// </summary>
    /// <param name="relativePath">相对路径</param>
    /// <param name="content">新内容</param>
    Task ReplaceFileAsync(string relativePath, string content);

    /// <summary>
    /// 读取知识文件内容
    /// </summary>
    /// <param name="relativePath">相对路径</param>
    /// <returns>文件内容，不存在返回 null</returns>
    Task<string?> ReadFileAsync(string relativePath);

    /// <summary>
    /// 删除知识文件
    /// </summary>
    /// <param name="relativePath">相对路径</param>
    Task DeleteFileAsync(string relativePath);

    /// <summary>
    /// 删除知识库目录（及其所有内容）
    /// </summary>
    /// <param name="relativePath">相对路径</param>
    Task DeleteDirectoryAsync(string relativePath);

    /// <summary>
    /// 搜索知识库
    /// </summary>
    /// <param name="query">搜索关键词</param>
    /// <param name="maxResults">最大结果数</param>
    /// <returns>搜索结果列表</returns>
    Task<List<KnowledgeSearchResult>> SearchAsync(string query, int maxResults = 5);

    /// <summary>
    /// 查找与给定内容语义相近的已有知识文件（写入前查重，避免重复建同类文件）。
    /// </summary>
    /// <param name="content">待写入的内容</param>
    /// <param name="minSimilarity">相似度下限（低于此值不算重复）</param>
    /// <param name="maxResults">最多返回的文件数</param>
    /// <returns>按相似度降序排列的相近文件；Embedding 未配置时返回空列表</returns>
    Task<List<SimilarKnowledgeFile>> FindSimilarFilesAsync(string content, double minSimilarity, int maxResults = 3);

    /// <summary>
    /// 基于已有向量离线聚类，找出疑似重复的文件组（供知识库定期整理）。
    /// </summary>
    /// <param name="minSimilarity">判定两个文件质心相似的下限</param>
    /// <returns>每组至少包含 2 个文件；Embedding 未配置或无重复时返回空列表</returns>
    Task<List<DuplicateFileCluster>> DetectDuplicateClustersAsync(double minSimilarity);

    /// <summary>
    /// 列出所有知识文件
    /// </summary>
    /// <returns>文件路径列表</returns>
    Task<List<string>> ListFilesAsync();

    /// <summary>
    /// 列出所有目录
    /// </summary>
    /// <returns>目录路径列表</returns>
    Task<List<string>> ListDirectoriesAsync();

    /// <summary>
    /// 检查文件是否存在
    /// </summary>
    /// <param name="relativePath">相对路径</param>
    /// <returns>是否存在</returns>
    Task<bool> FileExistsAsync(string relativePath);

    /// <summary>
    /// 使用 SEARCH/REPLACE 模式更新文件
    /// </summary>
    /// <param name="relativePath">文件相对路径</param>
    /// <param name="diffContent">包含 SEARCH/REPLACE 块的内容</param>
    /// <param name="fuzzyMatch">是否启用模糊匹配（忽略空格/缩进差异），默认 true</param>
    /// <returns>更新结果</returns>
    Task<FileUpdateResult> UpdateWithDiffAsync(string relativePath, string diffContent, bool fuzzyMatch = true);

    /// <summary>
    /// 刷新向量缓存（在文件变更后调用）
    /// </summary>
    Task RefreshVectorCacheAsync();

    /// <summary>清空派生向量数据，并使用当前 Embedding 配置立即全量重建全局知识库索引。</summary>
    Task<VectorIndexRebuildResult> RebuildVectorIndexAsync();
}
