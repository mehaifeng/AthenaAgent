using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>
/// 面向知识库页面的窄文件服务
/// 仅允许访问 AthenaData/KnowledgeBase 下的 Markdown 文件和目录
/// </summary>
public class KnowledgeBaseFileService : IKnowledgeBaseFileService
{
    private readonly IKnowledgeBaseService _knowledgeBaseService;
    private readonly IPlatformPathService _pathService;
    private readonly ILogger _logger;

    public KnowledgeBaseFileService(
        IKnowledgeBaseService knowledgeBaseService,
        IPlatformPathService pathService,
        ILogger logger)
    {
        _knowledgeBaseService = knowledgeBaseService;
        _pathService = pathService;
        _logger = logger.ForContext<KnowledgeBaseFileService>();
    }

    public Task<string?> ReadFileAsync(string absolutePath)
    {
        return _knowledgeBaseService.ReadFileAsync(ToRelativeMarkdownPath(absolutePath));
    }

    public async Task WriteFileAsync(string absolutePath, string content)
    {
        var relativePath = ToRelativeMarkdownPath(absolutePath);

        if (await _knowledgeBaseService.FileExistsAsync(relativePath))
        {
            await _knowledgeBaseService.ReplaceFileAsync(relativePath, content);
            return;
        }

        await _knowledgeBaseService.CreateFileAsync(relativePath, content);
    }

    public Task DeleteFileAsync(string absolutePath)
    {
        return _knowledgeBaseService.DeleteFileAsync(ToRelativeMarkdownPath(absolutePath));
    }

    public Task CreateDirectoryAsync(string absolutePath)
    {
        var fullPath = EnsureWithinKnowledgeBase(absolutePath);
        Directory.CreateDirectory(fullPath);
        return Task.CompletedTask;
    }

    public Task DeleteDirectoryAsync(string absolutePath)
    {
        return _knowledgeBaseService.DeleteDirectoryAsync(ToRelativeDirectoryPath(absolutePath));
    }

    private string ToRelativeMarkdownPath(string absolutePath)
    {
        var fullPath = EnsureWithinKnowledgeBase(absolutePath);
        if (!fullPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("知识库文件服务仅允许操作 Markdown 文件。");
        }

        return Path.GetRelativePath(GetKnowledgeBaseRoot(), fullPath)
            .Replace('\\', '/');
    }

    private string ToRelativeDirectoryPath(string absolutePath)
    {
        var fullPath = EnsureWithinKnowledgeBase(absolutePath);
        return Path.GetRelativePath(GetKnowledgeBaseRoot(), fullPath)
            .Replace('\\', '/');
    }

    private string EnsureWithinKnowledgeBase(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            throw new ArgumentException("路径不能为空", nameof(absolutePath));
        }

        var root = GetKnowledgeBaseRoot();
        var fullPath = Path.GetFullPath(absolutePath);
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        if (!fullPath.StartsWith(root, comparison))
        {
            _logger.Warning("阻止访问知识库目录之外的路径: {Path}", absolutePath);
            throw new UnauthorizedAccessException("仅允许访问知识库目录中的内容。");
        }

        return fullPath;
    }

    private string GetKnowledgeBaseRoot()
    {
        return Path.GetFullPath(_pathService.GetKnowledgeBaseDirectory());
    }
}
