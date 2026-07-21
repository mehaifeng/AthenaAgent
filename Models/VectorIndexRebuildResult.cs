namespace Athena.UI.Models;

/// <summary>向量索引重建后的持久化状态。</summary>
public sealed class VectorIndexRebuildResult
{
    public bool EmbeddingConfigured { get; init; }
    public int FileCount { get; init; }
    public int ChunkCount { get; init; }
    public int VectorCount { get; init; }
    public int FullyIndexedFileCount { get; init; }

    /// <summary>每个知识文件的所有分块均已取得向量；空知识库无需生成向量。</summary>
    public bool IsFullyIndexed =>
        EmbeddingConfigured &&
        (FileCount == 0 || (VectorCount == ChunkCount && FullyIndexedFileCount == FileCount));
}
