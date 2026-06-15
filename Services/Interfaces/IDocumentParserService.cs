using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 文档解析服务：将 PDF/Word/PPT/Excel 等文档解析为 AI 友好的 Markdown。
/// </summary>
public interface IDocumentParserService
{
    /// <summary>解析功能是否已在配置中启用。</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// 上传并解析本地文档，返回 Markdown 文本。整个流程为异步提交 + 轮询。
    /// </summary>
    /// <param name="filePath">本地文件完整路径。</param>
    /// <param name="fileName">文件名（含扩展名），用于判定文件类型。</param>
    Task<DocumentParseResult> ParseAsync(
        string filePath,
        string fileName,
        CancellationToken cancellationToken = default);
}
