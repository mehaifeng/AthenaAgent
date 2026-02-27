using System.Collections.Generic;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// Embedding 服务接口
/// 用于将文本转换为向量表示
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// 判断 Embedding 服务是否已配置
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// 生成文本的向量表示
    /// </summary>
    /// <param name="text">输入文本</param>
    /// <returns>向量数组（失败返回 null）</returns>
    Task<float[]?> GenerateEmbeddingAsync(string text);

    /// <summary>
    /// 批量生成文本的向量表示
    /// </summary>
    /// <param name="texts">输入文本列表</param>
    /// <returns>向量数组列表（与输入顺序对应）</returns>
    Task<List<float[]?>> GenerateEmbeddingsAsync(IEnumerable<string> texts);

    /// <summary>
    /// 计算两个向量之间的余弦相似度
    /// </summary>
    /// <param name="a">向量 A</param>
    /// <param name="b">向量 B</param>
    /// <returns>相似度（-1 到 1）</returns>
    float CosineSimilarity(float[] a, float[] b);
}
