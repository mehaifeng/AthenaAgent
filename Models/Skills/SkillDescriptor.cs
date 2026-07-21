using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.Models.Skills;

/// <summary>一个已发现的 Agent Skill；路径仅供本地服务使用，绝不由模型输入决定。</summary>
public sealed class SkillDescriptor
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string SkillFilePath { get; init; } = string.Empty;
    public string RootDirectory { get; init; } = string.Empty;
    public SkillSourceScope SourceScope { get; init; }
    public string SourceLabel => SourceScope == SkillSourceScope.Project ? "Project" : "Athena";
    public string StableKey { get; init; } = string.Empty;
    public string? Compatibility { get; init; }
    public string? AllowedTools { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> ResourceDirectories { get; init; } = Array.Empty<string>();
    public IReadOnlyList<SkillValidationIssue> ValidationIssues { get; init; } = Array.Empty<SkillValidationIssue>();
    public bool IsEnabled { get; init; }
    public bool IsEffective { get; init; }
    public string? ShadowedBy { get; init; }
    public string InstructionsPreview { get; init; } = string.Empty;
    public DateTime LastWriteTimeUtc { get; init; }

    public bool HasErrors => ValidationIssues.Any(issue => issue.IsError);
    public bool HasWarnings => ValidationIssues.Any(issue => !issue.IsError);
    public bool HasDiagnostics => ValidationIssues.Count != 0;
}
