using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Athena.UI.Models.Skills;
using Athena.UI.Services.Interfaces;
using Serilog;
using YamlDotNet.RepresentationModel;

namespace Athena.UI.Services.Skills;

/// <summary>
/// Discovers Agent Skills without executing them. A fresh snapshot is built for each chat
/// request so user edits become visible on the next turn.
/// </summary>
public sealed class SkillCatalogService : ISkillCatalogService
{
    private const int MaxSkillFileBytes = 256 * 1024;
    private const int MaxResourceBytes = 128 * 1024;
    private const int MaxSkillsPerScope = 200;
    private const int MaxImportFiles = 1000;
    private const long MaxImportBytes = 20L * 1024 * 1024;
    private const long MaxImportFileBytes = 4L * 1024 * 1024;
    private const int DescriptionModelLimit = 480;
    private readonly IPlatformPathService _paths;
    private readonly IConfigService _configService;
    private readonly IWorkspaceService? _workspaceService;
    private readonly ILogger _logger;

    public SkillCatalogService(
        IPlatformPathService paths,
        IConfigService configService,
        ILogger logger,
        IWorkspaceService? workspaceService = null)
    {
        _paths = paths;
        _configService = configService;
        _workspaceService = workspaceService;
        _logger = logger.ForContext<SkillCatalogService>();
        ApplicationSkillsDirectory = Path.Combine(_paths.GetAppDataDirectory(), "Skills");
        Directory.CreateDirectory(ApplicationSkillsDirectory);
    }

    public string ApplicationSkillsDirectory { get; }

    public SkillCatalogSnapshot GetSnapshot(string? workspaceDirectory = null, bool forceRefresh = false)
    {
        var config = _configService.Load();
        var application = DiscoverScope(ApplicationSkillsDirectory, SkillSourceScope.Application, config);
        var projectRoot = GetProjectSkillsDirectory(workspaceDirectory);
        var project = projectRoot == null
            ? new List<SkillDescriptor>()
            : DiscoverScope(projectRoot, SkillSourceScope.Project, config);

        // Project skills override same-name Athena skills. Invalid entries remain visible for diagnostics.
        var resolvedProject = ResolveScopeCollisions(project, "another project skill");
        var projectNames = new HashSet<string>(resolvedProject.Where(s => s.IsEffective).Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        var effective = new List<SkillDescriptor>(project.Count + application.Count);
        effective.AddRange(resolvedProject);
        effective.AddRange(ResolveScopeCollisions(application, "another Athena skill").Select(skill => projectNames.Contains(skill.Name)
            ? WithState(skill, isEffective: false, shadowedBy: "project skill")
            : skill));
        return new SkillCatalogSnapshot(effective
            .OrderByDescending(s => s.IsEffective)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    public async Task<SkillDescriptor?> FindEffectiveSkillAsync(string name, string? workspaceId)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var snapshot = GetSnapshot(await ResolveWorkspaceDirectoryAsync(workspaceId).ConfigureAwait(false));
        return snapshot.EffectiveSkills.FirstOrDefault(skill =>
            string.Equals(skill.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public async Task<SkillActivationResult?> ActivateAsync(string name, string? workspaceId)
    {
        var skill = await FindEffectiveSkillAsync(name, workspaceId).ConfigureAwait(false);
        if (skill == null) return null;

        try
        {
            var content = await File.ReadAllTextAsync(skill.SkillFilePath).ConfigureAwait(false);
            if (Encoding.UTF8.GetByteCount(content) > MaxSkillFileBytes) return null;
            var instructions = ExtractInstructions(content);
            var resources = GetResourceIndex(skill.RootDirectory);
            return new SkillActivationResult(skill, instructions, resources);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Unable to activate Skill {Skill}", name);
            return null;
        }
    }

    public async Task<SkillResourceResult?> ReadResourceAsync(string name, string relativePath, string? workspaceId)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return null;
        var skill = await FindEffectiveSkillAsync(name, workspaceId).ConfigureAwait(false);
        if (skill == null) return null;

        try
        {
            var target = Path.GetFullPath(Path.Combine(skill.RootDirectory, relativePath));
            if (!IsWithinRoot(target, skill.RootDirectory) || !File.Exists(target)) return null;
            var info = new FileInfo(target);
            if (info.Length > MaxResourceBytes || !IsTextResource(target)) return null;
            return new SkillResourceResult(relativePath.Replace('\\', '/'), await File.ReadAllTextAsync(target).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Unable to read resource {Resource} from Skill {Skill}", relativePath, name);
            return null;
        }
    }

    public async Task<SkillImportValidationResult> ValidateImportAsync(string sourcePath, bool isArchive)
    {
        var prepared = await PrepareImportAsync(sourcePath, isArchive).ConfigureAwait(false);
        try { return prepared.Result; }
        finally { prepared.Dispose(); }
    }

    public async Task<SkillImportValidationResult> ImportAsync(string sourcePath, bool isArchive)
    {
        var prepared = await PrepareImportAsync(sourcePath, isArchive).ConfigureAwait(false);
        string? destination = null;
        try
        {
            if (!prepared.Result.IsValid || prepared.Skill == null || prepared.RootDirectory == null)
                return prepared.Result;

            var name = prepared.Skill.Name;
            destination = Path.Combine(ApplicationSkillsDirectory, name);
            if (!IsSafeImportName(name))
                return new SkillImportValidationResult(false, "The Skill name is not safe to use as an import directory.");
            if (Directory.Exists(destination) || File.Exists(destination))
                return new SkillImportValidationResult(false, $"A Skill named '{name}' already exists in Athena Skills.", name);

            CopyValidatedDirectory(prepared.RootDirectory, destination);
            return prepared.Result with { Message = $"Imported Skill '{name}'." };
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrEmpty(destination) && Directory.Exists(destination))
            {
                try { Directory.Delete(destination, recursive: true); } catch { }
            }
            _logger.Warning(ex, "Failed to import Skill from {Source}", sourcePath);
            return new SkillImportValidationResult(false, $"Import failed: {ex.Message}");
        }
        finally { prepared.Dispose(); }
    }

    public Task<bool> DeleteSkillAsync(SkillDescriptor skill)
    {
        try
        {
            var isApplicationSkill = skill.SourceScope == SkillSourceScope.Application
                && IsDirectChildOf(skill.RootDirectory, ApplicationSkillsDirectory);
            var projectSkillsRoot = Path.GetDirectoryName(skill.RootDirectory);
            var isProjectSkill = skill.SourceScope == SkillSourceScope.Project
                && !string.IsNullOrEmpty(projectSkillsRoot)
                && string.Equals(Path.GetFileName(projectSkillsRoot), "skills", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileName(Path.GetDirectoryName(projectSkillsRoot)), ".agents", StringComparison.OrdinalIgnoreCase)
                && IsDirectChildOf(skill.RootDirectory, projectSkillsRoot);
            if (!isApplicationSkill && !isProjectSkill)
                return Task.FromResult(false);
            if (IsReparsePoint(skill.RootDirectory)) return Task.FromResult(false);
            if (Directory.EnumerateFileSystemEntries(skill.RootDirectory, "*", SearchOption.AllDirectories).Any(IsReparsePoint))
                return Task.FromResult(false);
            Directory.Delete(skill.RootDirectory, recursive: true);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to delete Skill {Skill}", skill.Name);
            return Task.FromResult(false);
        }
    }

    private List<SkillDescriptor> DiscoverScope(string root, SkillSourceScope scope, Models.AppConfig config)
    {
        var result = new List<SkillDescriptor>();
        try
        {
            if (!Directory.Exists(root)) return result;
            foreach (var directory in Directory.EnumerateDirectories(root).Take(MaxSkillsPerScope))
            {
                if (IsReparsePoint(directory)) continue;
                var skillPath = Path.Combine(directory, "SKILL.md");
                if (!File.Exists(skillPath) || IsReparsePoint(skillPath)) continue;
                result.Add(ParseSkill(skillPath, directory, scope, config));
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Unable to discover Skills under {Root}", root);
        }
        return result;
    }

    private SkillDescriptor ParseSkill(string skillPath, string root, SkillSourceScope scope, Models.AppConfig config)
    {
        var issues = new List<SkillValidationIssue>();
        var stableKey = $"{scope}:{Path.GetFullPath(root)}";
        var disabled = config.DisabledSkillKeys.Contains(stableKey, StringComparer.OrdinalIgnoreCase);
        try
        {
            var info = new FileInfo(skillPath);
            if (info.Length > MaxSkillFileBytes)
                return Invalid(root, skillPath, scope, stableKey, disabled, "SKILL.md exceeds the 256 KB safety limit.");

            var content = File.ReadAllText(skillPath);
            var (frontmatter, instructions) = SplitFrontmatter(content);
            var yaml = new YamlStream();
            yaml.Load(new StringReader(frontmatter));
            var map = yaml.Documents.FirstOrDefault()?.RootNode as YamlMappingNode;
            if (map == null) return Invalid(root, skillPath, scope, stableKey, disabled, "YAML frontmatter must be a mapping.");

            string GetScalar(string key) => map.Children
                .FirstOrDefault(pair => (pair.Key as YamlScalarNode)?.Value == key).Value is YamlScalarNode node
                    ? node.Value?.Trim() ?? string.Empty : string.Empty;
            var name = GetScalar("name");
            var description = GetScalar("description");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(description))
                return Invalid(root, skillPath, scope, stableKey, disabled, "Both name and description are required.");
            if (!string.Equals(name, Path.GetFileName(root), StringComparison.Ordinal))
                issues.Add(new SkillValidationIssue("The Skill name does not match its folder name.", false));
            if (description.Length > 1024)
                issues.Add(new SkillValidationIssue("Description exceeds the Agent Skills 1024-character recommendation.", false));

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var metadataNode = map.Children.FirstOrDefault(pair => (pair.Key as YamlScalarNode)?.Value == "metadata").Value as YamlMappingNode;
            if (metadataNode != null)
            {
                foreach (var pair in metadataNode.Children)
                {
                    if (pair.Key is YamlScalarNode key && pair.Value is YamlScalarNode value && !string.IsNullOrWhiteSpace(key.Value))
                        metadata[key.Value] = value.Value ?? string.Empty;
                }
            }

            return new SkillDescriptor
            {
                Name = name,
                Description = description.Length > DescriptionModelLimit ? description[..DescriptionModelLimit] + "…" : description,
                RootDirectory = Path.GetFullPath(root),
                SkillFilePath = skillPath,
                SourceScope = scope,
                StableKey = stableKey,
                Compatibility = GetScalar("compatibility"),
                AllowedTools = GetScalar("allowed-tools"),
                Metadata = metadata,
                ResourceDirectories = GetResourceDirectories(root),
                ValidationIssues = issues,
                IsEnabled = !disabled,
                InstructionsPreview = BuildPreview(instructions),
                LastWriteTimeUtc = info.LastWriteTimeUtc
            };
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to parse Skill {Path}", skillPath);
            return Invalid(root, skillPath, scope, stableKey, disabled, "Unable to parse SKILL.md frontmatter.");
        }
    }

    private static SkillDescriptor Invalid(string root, string path, SkillSourceScope scope, string stableKey, bool disabled, string error) => new()
    {
        Name = Path.GetFileName(root), RootDirectory = Path.GetFullPath(root), SkillFilePath = path,
        SourceScope = scope, StableKey = stableKey, IsEnabled = !disabled,
        ValidationIssues = new[] { new SkillValidationIssue(error, true) }
    };

    private static SkillDescriptor WithState(SkillDescriptor skill, bool isEffective, string? shadowedBy = null) => new()
    {
        Name = skill.Name, Description = skill.Description, SkillFilePath = skill.SkillFilePath, RootDirectory = skill.RootDirectory,
        SourceScope = skill.SourceScope, StableKey = skill.StableKey, Compatibility = skill.Compatibility, AllowedTools = skill.AllowedTools,
        Metadata = skill.Metadata, ResourceDirectories = skill.ResourceDirectories, ValidationIssues = skill.ValidationIssues,
        IsEnabled = skill.IsEnabled, IsEffective = isEffective && skill.IsEnabled, ShadowedBy = shadowedBy,
        InstructionsPreview = skill.InstructionsPreview, LastWriteTimeUtc = skill.LastWriteTimeUtc
    };

    private static IReadOnlyList<SkillDescriptor> ResolveScopeCollisions(IEnumerable<SkillDescriptor> skills, string shadowedBy)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return skills.OrderBy(skill => skill.RootDirectory, StringComparer.OrdinalIgnoreCase)
            .Select(skill => !skill.HasErrors && !seen.Add(skill.Name)
                ? WithState(skill, isEffective: false, shadowedBy: shadowedBy)
                : WithState(skill, isEffective: !skill.HasErrors))
            .ToArray();
    }

    private async Task<string?> ResolveWorkspaceDirectoryAsync(string? workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || _workspaceService == null) return null;
        return (await _workspaceService.LoadByIdAsync(workspaceId).ConfigureAwait(false))?.DirectoryPath;
    }

    private static string? GetProjectSkillsDirectory(string? workspaceDirectory)
    {
        if (string.IsNullOrWhiteSpace(workspaceDirectory)) return null;
        try
        {
            var root = Path.GetFullPath(workspaceDirectory);
            return Path.Combine(root, ".agents", "skills");
        }
        catch { return null; }
    }

    private static (string Frontmatter, string Instructions) SplitFrontmatter(string content)
    {
        var normalized = content.TrimStart('\uFEFF');
        using var reader = new StringReader(normalized);
        if (!string.Equals(reader.ReadLine()?.Trim(), "---", StringComparison.Ordinal))
            throw new FormatException("SKILL.md must begin with YAML frontmatter.");
        var yaml = new StringBuilder();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Trim() == "---") return (yaml.ToString(), reader.ReadToEnd().Trim());
            yaml.AppendLine(line);
        }
        throw new FormatException("SKILL.md frontmatter is not closed.");
    }

    private static string ExtractInstructions(string content) => SplitFrontmatter(content).Instructions;
    private static string BuildPreview(string instructions) => instructions.Length <= 600 ? instructions : instructions[..600] + "…";
    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static bool IsWithinRoot(string candidate, string root) => candidate.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool IsTextResource(string path) => new[] { ".md", ".txt", ".json", ".yaml", ".yml", ".csv", ".xml", ".html", ".cs", ".py", ".js", ".ts", ".ps1", ".sh" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    private static IReadOnlyList<string> GetResourceDirectories(string root) => new[] { "references", "assets", "scripts" }.Where(name => Directory.Exists(Path.Combine(root, name))).ToArray();
    private static IReadOnlyList<string> GetResourceIndex(string root) => GetResourceDirectories(root).SelectMany(dir => Directory.EnumerateFiles(Path.Combine(root, dir), "*", SearchOption.TopDirectoryOnly).Take(50).Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))).Take(100).ToArray();

    private async Task<PreparedSkillImport> PrepareImportAsync(string sourcePath, bool isArchive)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return PreparedSkillImport.Invalid("Select one Skill folder or ZIP archive first.");
        try
        {
            return isArchive
                ? await PrepareArchiveImportAsync(sourcePath).ConfigureAwait(false)
                : PrepareDirectoryImport(sourcePath);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Skill import validation failed for {Source}", sourcePath);
            return PreparedSkillImport.Invalid($"Skill validation failed: {ex.Message}");
        }
    }

    private PreparedSkillImport PrepareDirectoryImport(string sourcePath)
    {
        var root = Path.GetFullPath(sourcePath);
        if (!Directory.Exists(root) || IsReparsePoint(root)) return PreparedSkillImport.Invalid("The selected folder is unavailable or is a symbolic link.");
        return ValidateImportRoot(root, temporaryRoot: null);
    }

    private async Task<PreparedSkillImport> PrepareArchiveImportAsync(string sourcePath)
    {
        if (!File.Exists(sourcePath) || !Path.GetExtension(sourcePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            return PreparedSkillImport.Invalid("Select a ZIP archive containing exactly one Skill.");

        var staging = Path.Combine(Path.GetTempPath(), "AthenaSkillImport", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = ZipFile.OpenRead(sourcePath);
            var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList();
            if (files.Count == 0 || files.Count > MaxImportFiles)
                return PreparedSkillImport.Invalid($"The archive must contain between 1 and {MaxImportFiles} files.", staging);

            var paths = new List<string>(files.Count);
            long totalBytes = 0;
            foreach (var entry in files)
            {
                var relative = ValidateArchivePath(entry.FullName);
                if (relative == null) return PreparedSkillImport.Invalid("The archive contains an unsafe path.", staging);
                if (entry.Length > MaxImportFileBytes) return PreparedSkillImport.Invalid("The archive contains a file over the 4 MB limit.", staging);
                if (entry.CompressedLength == 0 && entry.Length > 0 || entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > 100)
                    return PreparedSkillImport.Invalid("The archive has an unsafe compression ratio.", staging);
                totalBytes += entry.Length;
                if (totalBytes > MaxImportBytes) return PreparedSkillImport.Invalid("The archive expands beyond the 20 MB limit.", staging);
                paths.Add(relative);
            }

            var skillPaths = paths.Where(path => path.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase) || string.Equals(path, "SKILL.md", StringComparison.OrdinalIgnoreCase)).ToList();
            if (skillPaths.Count != 1) return PreparedSkillImport.Invalid("The archive must contain exactly one SKILL.md at its root.", staging);
            var manifestParts = skillPaths[0].Split('/');
            if (manifestParts.Length > 2) return PreparedSkillImport.Invalid("SKILL.md must be at the archive root or one top-level Skill folder.", staging);
            var prefix = manifestParts.Length == 2 ? manifestParts[0] + "/" : string.Empty;
            if (prefix.Length != 0 && paths.Any(path => !path.StartsWith(prefix, StringComparison.Ordinal)))
                return PreparedSkillImport.Invalid("Archive files must all be inside the single Skill folder.", staging);

            foreach (var entry in files)
            {
                var relative = ValidateArchivePath(entry.FullName)!;
                var destination = Path.GetFullPath(Path.Combine(staging, relative));
                if (!IsWithinRoot(destination, staging)) return PreparedSkillImport.Invalid("The archive contains an unsafe path.", staging);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var source = entry.Open();
                await using var target = File.Create(destination);
                await source.CopyToAsync(target).ConfigureAwait(false);
            }
            var root = prefix.Length == 0 ? staging : Path.Combine(staging, manifestParts[0]);
            return ValidateImportRoot(root, staging, files.Count, totalBytes);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    private PreparedSkillImport ValidateImportRoot(string root, string? temporaryRoot, int archiveFileCount = 0, long archiveBytes = 0)
    {
        var files = EnumerateValidatedFiles(root).Take(MaxImportFiles + 1).ToList();
        if (files.Count == 0 || files.Count > MaxImportFiles)
            return PreparedSkillImport.Invalid($"A Skill must contain between 1 and {MaxImportFiles} files.", temporaryRoot);
        if (files.Any(file => Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length > 8))
            return PreparedSkillImport.Invalid("Skill folders may not be nested more than 8 levels deep.", temporaryRoot);
        var totalBytes = files.Sum(file => new FileInfo(file).Length);
        if (totalBytes > MaxImportBytes || files.Any(file => new FileInfo(file).Length > MaxImportFileBytes))
            return PreparedSkillImport.Invalid("Skill files exceed the import size limits.", temporaryRoot);
        var manifest = Path.Combine(root, "SKILL.md");
        if (!File.Exists(manifest)) return PreparedSkillImport.Invalid("The selected item must contain SKILL.md at its root.", temporaryRoot);
        var manifests = files.Where(file => string.Equals(Path.GetFileName(file), "SKILL.md", StringComparison.OrdinalIgnoreCase)).ToList();
        if (manifests.Count != 1 || !string.Equals(Path.GetFullPath(manifests[0]), Path.GetFullPath(manifest), StringComparison.OrdinalIgnoreCase))
            return PreparedSkillImport.Invalid("An imported folder must contain exactly one SKILL.md at its root.", temporaryRoot);

        var descriptor = ParseSkill(manifest, root, SkillSourceScope.Application, _configService.Load());
        if (descriptor.HasErrors) return PreparedSkillImport.Invalid(string.Join(" ", descriptor.ValidationIssues.Select(issue => issue.Message)), temporaryRoot);
        if (!IsSafeImportName(descriptor.Name)) return PreparedSkillImport.Invalid("Skill name must be 1-64 characters and cannot contain path separators or control characters.", temporaryRoot);
        return new PreparedSkillImport(new SkillImportValidationResult(true, "Skill package is valid.", descriptor.Name, archiveFileCount == 0 ? files.Count : archiveFileCount, archiveBytes == 0 ? totalBytes : archiveBytes), descriptor, root, temporaryRoot);
    }

    private static IEnumerable<string> EnumerateValidatedFiles(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            if (IsReparsePoint(entry)) throw new InvalidOperationException("Skill folders cannot contain symbolic links.");
            if (File.Exists(entry)) yield return entry;
        }
    }

    private static string? ValidateArchivePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains(":", StringComparison.Ordinal)) return null;
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => part is "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) ? null : string.Join('/', parts);
    }

    private static bool IsSafeImportName(string name) => name.Length is > 0 and <= 64
        && name.IndexOfAny(new[] { '/', '\\', ':', '\r', '\n', '\0' }) < 0
        && name is not "." and not ".."
        && string.Equals(name, name.Trim(), StringComparison.Ordinal)
        && string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal);

    private static bool IsDirectChildOf(string candidate, string parent) => string.Equals(Path.GetDirectoryName(Path.GetFullPath(candidate))?.TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static void CopyValidatedDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in EnumerateValidatedFiles(source))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private sealed class PreparedSkillImport : IDisposable
    {
        public PreparedSkillImport(SkillImportValidationResult result, SkillDescriptor? skill = null, string? rootDirectory = null, string? temporaryRoot = null)
        {
            Result = result; Skill = skill; RootDirectory = rootDirectory; TemporaryRoot = temporaryRoot;
        }
        public SkillImportValidationResult Result { get; }
        public SkillDescriptor? Skill { get; }
        public string? RootDirectory { get; }
        public string? TemporaryRoot { get; }
        public static PreparedSkillImport Invalid(string message, string? temporaryRoot = null) => new(new SkillImportValidationResult(false, message), temporaryRoot: temporaryRoot);
        public void Dispose()
        {
            if (!string.IsNullOrEmpty(TemporaryRoot) && Directory.Exists(TemporaryRoot))
            {
                try { Directory.Delete(TemporaryRoot, recursive: true); } catch { }
            }
        }
    }
}
