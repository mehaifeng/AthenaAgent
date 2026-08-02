using System;

namespace Athena.UI.Models;

/// <summary>
/// 工作区配置 — 绑定一个本地目录，为会话提供默认工作目录上下文。
/// </summary>
public class WorkspaceProfile
{
    /// <summary>唯一标识</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>用户可见的工作区名称</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>绑定的本地目录绝对路径</summary>
    public string DirectoryPath { get; set; } = string.Empty;

    /// <summary>系统管理的单一工作区知识文件名（位于 knowledge/ 下）</summary>
    public string KnowledgeFileName { get; set; } = "workspace.md";

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>最后更新时间</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>字段级可空覆盖；null 表示逐字段继承 App。</summary>
    public WorkspaceContextPolicyOverride? ContextPolicyOverride { get; set; }
}
