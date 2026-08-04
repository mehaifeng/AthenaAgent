using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>
/// 工作区服务实现 — 管理工作区配置和知识文件上下文注入
/// </summary>
public class WorkspaceService : IWorkspaceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IPlatformPathService _platformPathService;
    private readonly ILogger _logger;
    private readonly string _workspacesDirectory;
    private readonly IConfigService? _configService;
    private readonly IWorkspaceKnowledgeCompressor? _knowledgeCompressor;

    private WorkspaceProfile? _activeWorkspace;

    public WorkspaceProfile? ActiveWorkspace => _activeWorkspace;

    public event EventHandler<WorkspaceProfile?>? ActiveWorkspaceChanged;
    public event EventHandler<string>? WorkspacePolicyChanged;

    public WorkspaceService(
        IPlatformPathService platformPathService,
        ILogger logger,
        IConfigService? configService = null,
        IWorkspaceKnowledgeCompressor? knowledgeCompressor = null)
    {
        _platformPathService = platformPathService;
        _logger = logger.ForContext<WorkspaceService>();
        _configService = configService;
        _knowledgeCompressor = knowledgeCompressor;
        _workspacesDirectory = platformPathService.GetWorkspacesDirectory();
        Directory.CreateDirectory(_workspacesDirectory);
    }

    public async Task<List<WorkspaceProfile>> LoadAllAsync()
    {
        var result = new List<WorkspaceProfile>();
        try
        {
            var files = Directory.GetFiles(_workspacesDirectory, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var workspace = JsonSerializer.Deserialize<WorkspaceProfile>(json, JsonOptions);
                    if (workspace != null)
                    {
                        var previousKnowledgeFileName = workspace.KnowledgeFileName;
                        await EnsureKnowledgeFileAsync(workspace);
                        if (!string.Equals(previousKnowledgeFileName, workspace.KnowledgeFileName, StringComparison.Ordinal))
                        {
                            await WriteAtomicAsync(file, JsonSerializer.Serialize(workspace, JsonOptions));
                        }
                        result.Add(workspace);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to load workspace configuration: {File}", file);
                }
            }

            result = result.OrderBy(w => w.Name).ToList();
            _logger.Information("Loaded {Count} workspace(s)", result.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load workspace list");
        }

        return result;
    }

    public async Task<WorkspaceProfile?> LoadByIdAsync(string id)
    {
        var filePath = Path.Combine(_workspacesDirectory, $"{ValidateId(id)}.json");
        if (!File.Exists(filePath)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var workspace = JsonSerializer.Deserialize<WorkspaceProfile>(json, JsonOptions);
            if (workspace != null)
            {
                var previousKnowledgeFileName = workspace.KnowledgeFileName;
                await EnsureKnowledgeFileAsync(workspace);
                if (!string.Equals(previousKnowledgeFileName, workspace.KnowledgeFileName, StringComparison.Ordinal))
                {
                    await WriteAtomicAsync(filePath, JsonSerializer.Serialize(workspace, JsonOptions));
                }
            }
            return workspace;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load workspace: {Id}", id);
            return null;
        }
    }

    public async Task SaveAsync(WorkspaceProfile workspace)
    {
        workspace.Id = ValidateId(workspace.Id);
        await EnsureKnowledgeFileAsync(workspace);
        workspace.UpdatedAt = DateTime.Now;
        var filePath = Path.Combine(_workspacesDirectory, $"{workspace.Id}.json");
        var json = JsonSerializer.Serialize(workspace, JsonOptions);
        await WriteAtomicAsync(filePath, json);

        _logger.Information("Saved workspace: {Id} - {Name}", workspace.Id, workspace.Name);
        WorkspacePolicyChanged?.Invoke(this, workspace.Id);
    }

    public async Task UpdateContextPolicyAsync(
        WorkspaceProfile workspace,
        WorkspaceContextPolicyOverride? contextPolicyOverride,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var safeId = ValidateId(workspace.Id);
        var committedAt = DateTime.Now;
        var persisted = new WorkspaceProfile
        {
            Id = safeId,
            Name = workspace.Name,
            DirectoryPath = workspace.DirectoryPath,
            KnowledgeFileName = workspace.KnowledgeFileName,
            CreatedAt = workspace.CreatedAt,
            UpdatedAt = committedAt,
            ContextPolicyOverride = CloneContextPolicy(contextPolicyOverride)
        };
        await EnsureKnowledgeFileAsync(persisted);
        var filePath = Path.Combine(_workspacesDirectory, $"{safeId}.json");
        var json = JsonSerializer.Serialize(persisted, JsonOptions);
        await WriteAtomicAsync(filePath, json, cancellationToken);

        // Publish only after the durable write succeeds. Sessions hold this live object.
        workspace.Id = safeId;
        workspace.KnowledgeFileName = persisted.KnowledgeFileName;
        workspace.UpdatedAt = committedAt;
        workspace.ContextPolicyOverride = CloneContextPolicy(contextPolicyOverride);
        _logger.Information("Saved workspace context policy: {Id}", safeId);
        WorkspacePolicyChanged?.Invoke(this, safeId);
    }

    private static WorkspaceContextPolicyOverride? CloneContextPolicy(WorkspaceContextPolicyOverride? source) => source == null
        ? null
        : new WorkspaceContextPolicyOverride
        {
            ContextCapTokens = source.ContextCapTokens,
            AutoCompress = source.AutoCompress,
            CompressionThresholdTokens = source.CompressionThresholdTokens,
            KeepRecentRounds = source.KeepRecentRounds,
            TargetSummaryTokens = source.TargetSummaryTokens,
            WorkspaceKnowledgeTokenBudget = source.WorkspaceKnowledgeTokenBudget
        };

    private static async Task WriteAtomicAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path)
                        ?? throw new InvalidOperationException("Workspace path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temp,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
                // Preserve the original persistence exception.
            }
        }
    }

    public Task<bool> DeleteAsync(string id)
    {
        try
        {
            var safeId = ValidateId(id);
            var filePath = Path.Combine(_workspacesDirectory, $"{safeId}.json");
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            // 删除工作区知识目录
            var workspaceDir = Path.Combine(_workspacesDirectory, safeId);
            if (Directory.Exists(workspaceDir))
            {
                Directory.Delete(workspaceDir, recursive: true);
            }

            if (_activeWorkspace?.Id == id)
            {
                SetActiveWorkspace(null);
            }

            _logger.Information("Deleted workspace: {Id}", id);
            WorkspacePolicyChanged?.Invoke(this, safeId);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to delete workspace: {Id}", id);
            return Task.FromResult(false);
        }
    }

    private static string ValidateId(string id)
    {
        if (!Guid.TryParse(id, out var parsed))
            throw new ArgumentException("Workspace ID must be a GUID.", nameof(id));
        return parsed.ToString("N");
    }

    public async Task<WorkspaceProfile?> FindByDirectoryAsync(string directoryPath)
    {
        var all = await LoadAllAsync();
        var normalized = Path.GetFullPath(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return all.FirstOrDefault(w =>
            string.Equals(
                Path.GetFullPath(w.DirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                normalized,
                StringComparison.OrdinalIgnoreCase));
    }

    public void SetActiveWorkspace(WorkspaceProfile? workspace)
    {
        _activeWorkspace = workspace;
        ActiveWorkspaceChanged?.Invoke(this, workspace);
        _logger.Information("Activated workspace: {Name}", workspace?.Name ?? "(none)");
    }

    public string GetKnowledgeFilePath(WorkspaceProfile workspace)
    {
        workspace.Id = ValidateId(workspace.Id);
        var fileName = SanitizeKnowledgeFileName(workspace.KnowledgeFileName);
        return Path.Combine(_platformPathService.GetWorkspaceKnowledgeDirectory(workspace.Id), fileName);
    }

    public async Task<string?> GetKnowledgeFilePathAsync(string workspaceId)
    {
        var workspace = await LoadByIdAsync(workspaceId);
        return workspace == null ? null : GetKnowledgeFilePath(workspace);
    }

    public string? BuildWorkspaceKnowledgeContext(string workspaceId, string? knowledgeFilePath, int tokenBudget)
    {
        try
        {
            if (tokenBudget <= 0) return null;

            var path = knowledgeFilePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine(_platformPathService.GetWorkspaceKnowledgeDirectory(workspaceId), "workspace.md");
            }
            if (!IsWorkspaceKnowledgeFile(path) || !File.Exists(path)) return null;

            var content = File.ReadAllText(path);
            var fileName = Path.GetFileNameWithoutExtension(path);
            var header = $"### {fileName}\n";
            if (ConversationContext.EstimateTokens(header + content) <= tokenBudget)
            {
                return header + content;
            }

            var low = 0;
            var high = content.Length;
            while (low < high)
            {
                var mid = (low + high + 1) / 2;
                if (ConversationContext.EstimateTokens(header + content[..mid]) <= tokenBudget) low = mid;
                else high = mid - 1;
            }
            return low == 0 ? null : header + content[..low].TrimEnd();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to build workspace knowledge context: {WorkspaceId}", workspaceId);
            return null;
        }
    }

    public async Task EnforceKnowledgeFileBudgetAsync(string fullPath, CancellationToken ct = default)
    {
        if (_configService == null || _knowledgeCompressor == null || !IsWorkspaceKnowledgeFile(fullPath) || !File.Exists(fullPath)) return;

        var budget = _configService.Load().WorkspaceKnowledgeTokenBudget;
        if (budget <= 0) return; // 0 表示禁用工作区知识注入，不应为此删除本地知识。

        string content;
        try
        {
            content = await File.ReadAllTextAsync(fullPath, ct);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to read workspace knowledge file for budget check: {Path}", fullPath);
            return;
        }

        var fileName = Path.GetFileNameWithoutExtension(fullPath);
        var contentBudget = Math.Max(1, budget - ConversationContext.EstimateTokens($"### {fileName}\n"));
        var originalTokens = ConversationContext.EstimateTokens(content);
        if (originalTokens <= contentBudget) return;

        var compressed = await _knowledgeCompressor.CompressAsync(content, contentBudget, ct);
        if (string.IsNullOrWhiteSpace(compressed))
        {
            _logger.Warning("Workspace knowledge exceeds budget but compression failed; keeping original file: {Path} ({Tokens}/{Budget})",
                fullPath, originalTokens, budget);
            return;
        }

        var bounded = TruncateToTokenBudget(compressed, contentBudget);
        try
        {
            await File.WriteAllTextAsync(fullPath, bounded, ct);
            _logger.Information("Workspace knowledge file compressed: {Path} ({Before} -> {After}, budget {Budget})",
                fullPath, originalTokens, ConversationContext.EstimateTokens(bounded), contentBudget);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to write compressed workspace knowledge file: {Path}", fullPath);
        }
    }

    private static string TruncateToTokenBudget(string content, int tokenBudget)
    {
        if (ConversationContext.EstimateTokens(content) <= tokenBudget) return content;

        int low = 0;
        int high = content.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (ConversationContext.EstimateTokens(content[..mid]) <= tokenBudget)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low == 0 ? string.Empty : content[..low].TrimEnd();
    }

    private bool IsWorkspaceKnowledgeFile(string fullPath)
    {
        var workspaceRoot = Path.GetFullPath(_workspacesDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(fullPath);
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!normalizedPath.StartsWith(workspaceRoot, comparison) || !normalizedPath.EndsWith(".md", comparison)) return false;

        var relative = Path.GetRelativePath(workspaceRoot, normalizedPath);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Length < 3 || !string.Equals(segments[1], "knowledge", comparison)) return false;

        var knowledgeRoot = Path.GetFullPath(_platformPathService.GetWorkspaceKnowledgeDirectory(segments[0]))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(knowledgeRoot, comparison);
    }

    private async Task EnsureKnowledgeFileAsync(WorkspaceProfile workspace)
    {
        var knowledgeDir = _platformPathService.GetWorkspaceKnowledgeDirectory(workspace.Id);
        Directory.CreateDirectory(knowledgeDir);

        if (string.IsNullOrWhiteSpace(workspace.KnowledgeFileName) || workspace.KnowledgeFileName == "workspace.md")
        {
            var existingFiles = Directory.GetFiles(knowledgeDir, "*.md", SearchOption.TopDirectoryOnly);
            var preferred = existingFiles.FirstOrDefault(path =>
                string.Equals(Path.GetFileName(path), "workspace.md", StringComparison.OrdinalIgnoreCase))
                ?? existingFiles.OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
            workspace.KnowledgeFileName = preferred == null ? "workspace.md" : Path.GetFileName(preferred);
        }

        var fullPath = GetKnowledgeFilePath(workspace);
        if (!File.Exists(fullPath))
        {
            await File.WriteAllTextAsync(fullPath, $"# {workspace.Name} Workspace Knowledge\n");
        }
    }

    private static string SanitizeKnowledgeFileName(string? fileName)
    {
        var sanitized = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(sanitized) || !sanitized.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return "workspace.md";
        }
        return sanitized;
    }
}
