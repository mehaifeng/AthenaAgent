using System;

namespace Athena.UI.Models;

/// <summary>
/// 知识库定期整理 Agent 的模型来源。
/// InheritSecondary：复用次级（后台任务）模型——默认；
/// InheritMain：复用主 AI 模型；
/// Custom：使用整理专属字段（留空时逐字段回退次级 → 主 AI）。
/// </summary>
/// <summary>
/// 知识库整理的运行态（持久化到 AthenaData/kb_maintenance_state.json，与用户配置分离）。
/// </summary>
public sealed class KnowledgeMaintenanceState
{
    /// <summary>上次成功完成一轮整理的时间（UTC）。null 表示从未运行。</summary>
    public DateTime? LastRunUtc { get; set; }

    /// <summary>上次运行结果：Succeeded / NoDuplicates / Failed / Skipped。</summary>
    public string? LastOutcome { get; set; }

    /// <summary>上次运行的简报（合并/删除概览或错误信息）。</summary>
    public string? LastReport { get; set; }

    /// <summary>上次运行合并的文件数（可观测性）。</summary>
    public int LastMergedGroups { get; set; }
}
