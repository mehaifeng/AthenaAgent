using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 知识库文件服务接口
/// 仅允许访问知识库目录下的 Markdown 文件及其子目录
/// </summary>
public interface IKnowledgeBaseFileService
{
    Task<string?> ReadFileAsync(string absolutePath);
    Task WriteFileAsync(string absolutePath, string content);
    Task DeleteFileAsync(string absolutePath);
    Task CreateDirectoryAsync(string absolutePath);
    Task DeleteDirectoryAsync(string absolutePath);
}
