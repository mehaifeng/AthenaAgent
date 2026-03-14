using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>
/// 本地系统文件服务实现类
/// </summary>
public class FileSystemService : IFileSystemService
{
    private readonly IConfigService _configService;
    private readonly IPlatformPathService _pathService;
    private readonly ILogger _logger;
    private const string SearchStart = "<<<<<<< SEARCH";
    private const string Separator = "=======";
    private const string ReplaceEnd = ">>>>>>> REPLACE";

    public FileSystemService(IConfigService configService, IPlatformPathService pathService, ILogger logger)
    {
        _configService = configService;
        _pathService = pathService;
        _logger = logger.ForContext<FileSystemService>();
    }

    /// <summary>
    /// 解析路径中的环境变量和波浪号
    /// </summary>
    private string ExpandPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;

        // 展开环境变量 (如 %USERPROFILE%)
        var expanded = Environment.ExpandEnvironmentVariables(path);

        // 展开波浪号 ~
        if (expanded.StartsWith("~"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                expanded = expanded.Length == 1 
                    ? home 
                    : Path.Combine(home, expanded.Substring(2));
            }
        }
        else if (!Path.IsPathRooted(expanded) && !expanded.StartsWith("%"))
        {
            // 如果是相对路径，且不以环境变量开头，进行智能锚定
            
            // 1. 优先尝试知识库目录 (与 KnowledgeBaseService 行为对齐)
            var kbDir = _pathService.GetKnowledgeBaseDirectory();
            var kbPath = Path.GetFullPath(Path.Combine(kbDir, expanded));
            if (File.Exists(kbPath) || Directory.Exists(kbPath))
            {
                return kbPath;
            }

            // 2. 其次尝试应用数据根目录
            var appDataDir = _pathService.GetAppDataDirectory();
            var appDataPath = Path.GetFullPath(Path.Combine(appDataDir, expanded));
            if (File.Exists(appDataPath) || Directory.Exists(appDataPath))
            {
                return appDataPath;
            }

            // 3. 如果文件尚不存在（例如 Write 操作），默认锚定到知识库
            return kbPath;
        }

        return expanded;
    }

    /// <summary>
    /// 获取当前平台的访问规则配置
    /// </summary>
    private PlatformFileSystemConfig GetCurrentPlatformConfig(FileSystemPolicyConfig policy)
    {
        if (OperatingSystem.IsWindows()) return policy.Platforms.Windows;
        if (OperatingSystem.IsMacOS()) return policy.Platforms.MacOS;
        if (OperatingSystem.IsLinux()) return policy.Platforms.Linux;
        
        throw new PlatformNotSupportedException("Unsupported Operating System for file access rules.");
    }

    /// <summary>
    /// 检查路径是否在受限/允许的目录列表中
    /// </summary>
    private bool IsPathInDirectoryList(string fullPath, List<string> directoryList)
    {
        var comparison = OperatingSystem.IsLinux() 
            ? StringComparison.Ordinal 
            : StringComparison.OrdinalIgnoreCase;

        foreach (var dir in directoryList)
        {
            var expandedDir = Path.GetFullPath(ExpandPath(dir));
            // 路径前缀匹配
            if (fullPath.StartsWith(expandedDir, comparison))
            {
                // 确保是真正的子目录，防止 C:\App 匹配 C:\AppBackup
                if (fullPath.Length == expandedDir.Length || 
                    fullPath[expandedDir.Length] == Path.DirectorySeparatorChar || 
                    fullPath[expandedDir.Length] == Path.AltDirectorySeparatorChar)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 检查扩展名是否匹配
    /// </summary>
    private bool IsExtensionMatch(string fullPath, List<string> extensions)
    {
        var ext = Path.GetExtension(fullPath)?.ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) return false;
        
        if (!ext.StartsWith(".")) ext = "." + ext;
        return extensions.Contains(ext);
    }

    /// <summary>
    /// 验证路径的安全性
    /// </summary>
    private void ValidatePathAndSecurity(string absolutePath, bool isWriteOperation, bool isDirectoryOperation = false, long dataSize = 0)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            throw new ArgumentException("文件路径不能为空");

        var policy = _configService.Load().FileSystemPolicy;
        var global = policy.Global;
        var platform = GetCurrentPlatformConfig(policy);

        // 1. 解析和规范化路径，防范路径穿越
        var expandedPath = ExpandPath(absolutePath);
        var fullPath = Path.GetFullPath(expandedPath);

        // 如果不跟随符号链接，验证真实路径
        if (!global.FollowSymlinks && (File.Exists(fullPath) || Directory.Exists(fullPath)))
        {
            var targetInfo = File.Exists(fullPath) ? new FileInfo(fullPath).ResolveLinkTarget(true) : new DirectoryInfo(fullPath).ResolveLinkTarget(true);
            if (targetInfo != null)
            {
                fullPath = Path.GetFullPath(targetInfo.FullName);
            }
        }

        // 记录审计日志
        _logger.Information("审计日志: 请求路径={RequestedPath}, 规范路径={FullPath}, 操作={Operation}", absolutePath, fullPath, isWriteOperation ? "写入" : "读取");

        // 预处理特殊硬编码拦截 (Linux /proc, /sys)
        if (OperatingSystem.IsLinux() && (fullPath.StartsWith("/proc/") || fullPath.StartsWith("/sys/")))
        {
            _logger.Warning("安全拦截: 尝试访问 Linux 虚拟文件系统 路径: {Path}", fullPath);
            throw new UnauthorizedAccessException($"禁止访问虚拟文件系统: {fullPath}");
        }

        // 保护应用程序自身的配置文件，严禁通过文件系统工具修改
        if (isWriteOperation)
        {
            var configPath = Path.GetFullPath(_pathService.GetConfigFilePath());
            if (fullPath.Equals(configPath, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warning("安全拦截: AI 尝试篡改自身配置文件 路径: {Path}", fullPath);
                throw new UnauthorizedAccessException("系统自我保护机制：禁止通过文件系统工具修改应用配置文件！");
            }
        }

        // 隐藏文件拦截
        if (!global.AllowHiddenFiles && (File.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.Hidden) == FileAttributes.Hidden))
        {
             _logger.Warning("安全拦截: 尝试访问隐藏文件 路径: {Path}", fullPath);
             throw new UnauthorizedAccessException($"策略禁止访问隐藏文件: {fullPath}");
        }

        var accessRules = isWriteOperation ? platform.WriteAccess : platform.ReadAccess;

        // 规则 1: 校验 global.blockedExtensions (立即拒绝)
        if (!isDirectoryOperation && IsExtensionMatch(fullPath, global.BlockedExtensions))
        {
            _logger.Warning("安全拦截 [BlockedExt]: 触发扩展名黑名单 路径: {Path}", fullPath);
            throw new UnauthorizedAccessException($"由于安全策略，禁止访问此类扩展名的文件: {Path.GetExtension(fullPath)}。请勿尝试读取或修改此文件。");
        }

        // 规则 2: 校验平台 blockedDirectories (立即拒绝)
        if (IsPathInDirectoryList(fullPath, accessRules.BlockedDirectories))
        {
            _logger.Warning("安全拦截 [BlockedDir]: 触发目录黑名单 路径: {Path}", fullPath);
            throw new UnauthorizedAccessException($"由于安全策略，系统关键目录受到保护，访问被拒绝。请不要在此目录进行读写操作。");
        }

        // 规则 3: 校验文件大小限制
        long targetLimit = isWriteOperation ? global.MaxWriteSizeBytes : global.MaxReadSizeBytes;
        
        if (dataSize > 0 && dataSize > targetLimit)
        {
            _logger.Warning("安全拦截 [SizeLimit]: 尝试操作超出限制大小的数据 大小: {Size}, 路径: {Path}", dataSize, fullPath);
            throw new InvalidOperationException($"数据大小 ({dataSize} bytes) 超过策略限制 ({targetLimit} bytes)。请尝试分批处理或请求用户协助。");
        }
        
        if (!isWriteOperation && !isDirectoryOperation && File.Exists(fullPath))
        {
            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length > targetLimit)
            {
                _logger.Warning("安全拦截 [SizeLimit]: 目标文件超出读取限制 大小: {Size}, 路径: {Path}", fileInfo.Length, fullPath);
                throw new InvalidOperationException($"文件大小 ({fileInfo.Length} bytes) 超过策略读取限制 ({targetLimit} bytes)。文件过大，无法直接载入上下文。");
            }
        }
        
        // 放行
        _logger.Information("审计日志: 规则验证通过，操作放行。路径={FullPath}", fullPath);
    }

    public async Task<string?> ReadFileAsync(string absolutePath)
    {
        try
        {
            ValidatePathAndSecurity(absolutePath, isWriteOperation: false);
            var fullPath = Path.GetFullPath(ExpandPath(absolutePath));

            if (!File.Exists(fullPath))
            {
                _logger.Warning("Read - 尝试读取不存在的文件: {Path}", fullPath);
                return null;
            }

            return await File.ReadAllTextAsync(fullPath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Read - 读取文件失败: {Path}", absolutePath);
            throw;
        }
    }

    public async Task<bool> WriteFileAsync(string absolutePath, string content)
    {
        try
        {
            var contentBytes = Encoding.UTF8.GetByteCount(content ?? string.Empty);
            ValidatePathAndSecurity(absolutePath, isWriteOperation: true, dataSize: contentBytes);
            var fullPath = Path.GetFullPath(ExpandPath(absolutePath));

            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                var policy = _configService.Load().FileSystemPolicy.Global;
                if (!Directory.Exists(directory) && !policy.AllowDirectoryCreation)
                {
                    throw new UnauthorizedAccessException($"策略禁止自动创建目录: {directory}");
                }
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(fullPath, content ?? string.Empty, Encoding.UTF8);
            _logger.Information("Write - 全量写入文件成功: {Path}", fullPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Write - 全量写入文件失败: {Path}", absolutePath);
            throw;
        }
    }

    public Task<bool> DeleteFileAsync(string absolutePath)
    {
        try
        {
            var policy = _configService.Load().FileSystemPolicy.Global;
            if (!policy.AllowDelete)
            {
                throw new UnauthorizedAccessException("策略已禁止所有文件删除操作");
            }

            ValidatePathAndSecurity(absolutePath, isWriteOperation: true);
            var fullPath = Path.GetFullPath(ExpandPath(absolutePath));

            if (!File.Exists(fullPath))
            {
                _logger.Warning("Delete - 尝试删除不存在的文件: {Path}", fullPath);
                return Task.FromResult(false);
            }

            File.Delete(fullPath);
            _logger.Information("Delete - 成功删除文件: {Path}", fullPath);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Delete - 删除文件失败: {Path}", absolutePath);
            throw;
        }
    }

    public Task<List<string>> ListDirectoryAsync(string absolutePath, bool recursive = false)
    {
        try
        {
            ValidatePathAndSecurity(absolutePath, isWriteOperation: false, isDirectoryOperation: true);
            var fullPath = Path.GetFullPath(ExpandPath(absolutePath));
            
            if (!Directory.Exists(fullPath))
            {
                _logger.Warning("ListDir - 尝试列出不存在的目录: {Path}", fullPath);
                return Task.FromResult(new List<string>());
            }

            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var entries = new List<string>();
            var dirInfo = new DirectoryInfo(fullPath);
            
            // 为了安全，即使是 ListDir，遍历结果也需要受到过滤（如隐藏文件限制等）
            var policy = _configService.Load().FileSystemPolicy.Global;

            foreach (var dir in dirInfo.GetDirectories("*", searchOption))
            {
                if (!policy.AllowHiddenFiles && (dir.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden) continue;
                entries.Add($"[DIR] {dir.FullName}");
            }
            
            foreach (var file in dirInfo.GetFiles("*", searchOption))
            {
                if (!policy.AllowHiddenFiles && (file.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden) continue;
                entries.Add($"[FILE] {file.FullName}");
            }

            _logger.Information("ListDir - 列出目录: {Path}, 包含 {Count} 个条目", fullPath, entries.Count);
            return Task.FromResult(entries);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "ListDir - 列出目录失败: {Path}", absolutePath);
            throw;
        }
    }

    public async Task<FileUpdateResult> ModifyFileWithDiffAsync(string absolutePath, string diffContent, bool fuzzyMatch = true)
    {
        try
        {
            // 对于 Modify，我们既需要读取（Read）也需要写入（Write）权限
            // 先通过写入权限验证（写入权限通常更严格）
            ValidatePathAndSecurity(absolutePath, isWriteOperation: true);
            var fullPath = Path.GetFullPath(ExpandPath(absolutePath));

            if (!File.Exists(fullPath))
            {
                return new FileUpdateResult { Success = false, Message = $"文件不存在: {fullPath}" };
            }

            var fileContent = await File.ReadAllTextAsync(fullPath, Encoding.UTF8);
            var blocks = ParseDiffBlocks(diffContent);

            if (blocks.Count == 0)
            {
                return new FileUpdateResult
                {
                    Success = false,
                    Message = "未找到有效的 SEARCH/REPLACE 块。\n请使用以下格式：\n<<<<<<< SEARCH\n要查找的原始内容\n=======\n替换后的新内容\n>>>>>>> REPLACE"
                };
            }

            var modifiedContent = fileContent;
            var appliedCount = 0;

            foreach (var block in blocks)
            {
                var matchResult = ApplyDiffBlock(modifiedContent, block, fuzzyMatch);

                if (!matchResult.Success)
                {
                    return matchResult;
                }

                modifiedContent = matchResult.ModifiedContent!;
                appliedCount++;
            }

            var contentBytes = Encoding.UTF8.GetByteCount(modifiedContent);
            var policy = _configService.Load().FileSystemPolicy.Global;
            
            if (contentBytes > policy.MaxWriteSizeBytes)
            {
                 return new FileUpdateResult { Success = false, Message = $"修改后的内容超出文件大小限制" };
            }

            await File.WriteAllTextAsync(fullPath, modifiedContent, Encoding.UTF8);

            _logger.Information("Modify - 成功应用 {Count} 个 Diff 块到文件: {Path}", appliedCount, fullPath);

            return new FileUpdateResult
            {
                Success = true,
                Message = $"成功应用 {appliedCount} 个修改",
                AppliedBlocks = appliedCount
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Modify - Diff 替换更新失败: {Path}", absolutePath);
            return new FileUpdateResult
            {
                Success = false,
                Message = $"更新失败: {ex.Message}"
            };
        }
    }

    private class DiffBlock
    {
        public string SearchContent { get; set; } = string.Empty;
        public string ReplaceContent { get; set; } = string.Empty;
    }

    private List<DiffBlock> ParseDiffBlocks(string diffContent)
    {
        var blocks = new List<DiffBlock>();
        var lines = diffContent.Split('\n');

        int i = 0;
        while (i < lines.Length)
        {
            if (lines[i].TrimStart().StartsWith(SearchStart))
            {
                var block = new DiffBlock();
                i++;

                var searchLines = new List<string>();
                while (i < lines.Length && !lines[i].Trim().StartsWith(Separator))
                {
                    searchLines.Add(lines[i]);
                    i++;
                }
                block.SearchContent = string.Join("\n", searchLines).Trim('\r', '\n');

                if (i < lines.Length && lines[i].Trim().StartsWith(Separator))
                    i++;

                var replaceLines = new List<string>();
                while (i < lines.Length && !lines[i].TrimStart().StartsWith(ReplaceEnd))
                {
                    replaceLines.Add(lines[i]);
                    i++;
                }
                block.ReplaceContent = string.Join("\n", replaceLines).Trim('\r', '\n');

                if (i < lines.Length && lines[i].TrimStart().StartsWith(ReplaceEnd))
                    i++;

                if (!string.IsNullOrEmpty(block.SearchContent))
                {
                    blocks.Add(block);
                }
            }
            else
            {
                i++;
            }
        }

        return blocks;
    }

    private FileUpdateResult ApplyDiffBlock(string content, DiffBlock block, bool fuzzyMatch)
    {
        var exactIndex = content.IndexOf(block.SearchContent);

        if (exactIndex >= 0)
        {
            var secondMatch = content.IndexOf(block.SearchContent, exactIndex + 1);
            if (secondMatch >= 0)
            {
                var matches = FindAllMatches(content, block.SearchContent);
                return new FileUpdateResult
                {
                    Success = false,
                    Message = $"找到 {matches.Count} 处匹配，请提供更多上下文以唯一标识要修改的位置",
                    MultipleMatches = matches
                };
            }

            var newContent = content.Substring(0, exactIndex)
                           + block.ReplaceContent
                           + content.Substring(exactIndex + block.SearchContent.Length);

            return new FileUpdateResult
            {
                Success = true,
                ModifiedContent = newContent,
                LineNumber = content.Substring(0, exactIndex).Split('\n').Length
            };
        }

        if (fuzzyMatch)
        {
            return FuzzyMatchAndReplace(content, block);
        }

        return new FileUpdateResult
        {
            Success = false,
            Message = "未找到匹配的 SEARCH 内容。\n请确保 SEARCH 块与文件中的原始内容完全一致。"
        };
    }

    private FileUpdateResult FuzzyMatchAndReplace(string content, DiffBlock block)
    {
        var normalizedContent = NormalizeForComparison(content);
        var normalizedSearch = NormalizeForComparison(block.SearchContent);

        var index = normalizedContent.Normalized.IndexOf(normalizedSearch.Normalized);
        if (index < 0)
        {
            var similarity = CalculateSimilarity(normalizedContent.Normalized, normalizedSearch.Normalized);
            return new FileUpdateResult
            {
                Success = false,
                Message = $"未找到匹配内容（相似度: {similarity:P1}）。\n请确保 SEARCH 块与文件中的原始内容尽量一致。"
            };
        }

        var originalStart = normalizedContent.PositionMap[index];
        var searchEndIndex = Math.Min(index + normalizedSearch.Normalized.Length - 1, normalizedContent.PositionMap.Length - 1);
        var originalEnd = normalizedContent.PositionMap[searchEndIndex];

        var secondMatch = normalizedContent.Normalized.IndexOf(normalizedSearch.Normalized, index + 1);
        if (secondMatch >= 0)
        {
            return new FileUpdateResult
            {
                Success = false,
                Message = "找到多处模糊匹配，请提供更多上下文以唯一标识要修改的位置"
            };
        }

        var newContent = content.Substring(0, originalStart)
                       + block.ReplaceContent
                       + content.Substring(originalEnd + 1);

        return new FileUpdateResult
        {
            Success = true,
            ModifiedContent = newContent,
            LineNumber = content.Substring(0, originalStart).Split('\n').Length
        };
    }

    private (string Normalized, int[] PositionMap) NormalizeForComparison(string text)
    {
        var normalized = new StringBuilder();
        var positionMap = new List<int>();

        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsWhiteSpace(c))
            {
                normalized.Append(char.ToLower(c));
                positionMap.Add(i);
            }
        }

        return (normalized.ToString(), positionMap.ToArray());
    }

    private static double CalculateSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 1.0;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;

        var distance = LevenshteinDistance(a, b);
        var maxLen = Math.Max(a.Length, b.Length);
        return 1.0 - (double)distance / maxLen;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        var matrix = new int[a.Length + 1, b.Length + 1];

        for (int i = 0; i <= a.Length; i++) matrix[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) matrix[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                matrix[i, j] = Math.Min(
                    Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                    matrix[i - 1, j - 1] + cost);
            }
        }

        return matrix[a.Length, b.Length];
    }

    private List<string> FindAllMatches(string content, string search)
    {
        var matches = new List<string>();
        var index = 0;

        while ((index = content.IndexOf(search, index)) != -1)
        {
            var start = Math.Max(0, index - 20);
            var end = Math.Min(content.Length, index + search.Length + 20);
            matches.Add($"...{content.Substring(start, end - start)}...");
            index++;
        }

        return matches;
    }
}
