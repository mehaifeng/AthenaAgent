using System.Collections.Generic;
using System.Threading.Tasks;
using Athena.UI.Models.Skills;

namespace Athena.UI.Services.Interfaces;

public interface ISkillCatalogService
{
    string ApplicationSkillsDirectory { get; }

    SkillCatalogSnapshot GetSnapshot(string? workspaceDirectory = null, bool forceRefresh = false);

    Task<SkillDescriptor?> FindEffectiveSkillAsync(string name, string? workspaceId);

    Task<SkillActivationResult?> ActivateAsync(string name, string? workspaceId);

    Task<SkillResourceResult?> ReadResourceAsync(string name, string relativePath, string? workspaceId);

    Task<SkillImportValidationResult> ValidateImportAsync(string sourcePath, bool isArchive);

    Task<SkillImportValidationResult> ImportAsync(string sourcePath, bool isArchive);

    Task<bool> DeleteSkillAsync(SkillDescriptor skill);
}

public sealed record SkillActivationResult(
    SkillDescriptor Skill,
    string Instructions,
    IReadOnlyList<string> ResourceIndex);

public sealed record SkillResourceResult(string RelativePath, string Content);

public sealed record SkillImportValidationResult(
    bool IsValid,
    string Message,
    string? SkillName = null,
    int FileCount = 0,
    long TotalBytes = 0);
