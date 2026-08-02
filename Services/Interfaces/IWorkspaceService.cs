using Athena.UI.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 工作区服务接口 — 管理工作区配置和知识文件上下文注入
/// </summary>
public interface IWorkspaceService
{
    /// <summary>加载所有工作区配置</summary>
    Task<List<WorkspaceProfile>> LoadAllAsync();

    /// <summary>按 ID 加载工作区</summary>
    Task<WorkspaceProfile?> LoadByIdAsync(string id);

    /// <summary>保存（创建或更新）工作区</summary>
    Task SaveAsync(WorkspaceProfile workspace);

    /// <summary>
    /// Durably writes a draft field-level context policy, then publishes it to the live
    /// Workspace object and raises WorkspacePolicyChanged. A failed write leaves the live object untouched.
    /// </summary>
    Task UpdateContextPolicyAsync(
        WorkspaceProfile workspace,
        WorkspaceContextPolicyOverride? contextPolicyOverride,
        CancellationToken cancellationToken = default);

    /// <summary>删除工作区配置和受管知识目录；历史会话保留原工作区 ID，并在工作区不存在时显示为未分组。</summary>
    Task<bool> DeleteAsync(string id);

    /// <summary>根据目录路径查找已有工作区（避免重复创建）</summary>
    Task<WorkspaceProfile?> FindByDirectoryAsync(string directoryPath);

    /// <summary>当前激活的工作区（null 表示未选择）</summary>
    WorkspaceProfile? ActiveWorkspace { get; }

    /// <summary>设置当前激活工作区</summary>
    void SetActiveWorkspace(WorkspaceProfile? workspace);

    /// <summary>激活工作区变更事件</summary>
    event System.EventHandler<WorkspaceProfile?>? ActiveWorkspaceChanged;

    /// <summary>工作区字段级策略保存或删除后发布；会话据此重算下一请求策略。</summary>
    event System.EventHandler<string>? WorkspacePolicyChanged;

    /// <summary>获取工作区受管知识文件的绝对路径</summary>
    string GetKnowledgeFilePath(WorkspaceProfile workspace);

    /// <summary>按 ID 获取工作区受管知识文件的绝对路径</summary>
    Task<string?> GetKnowledgeFilePathAsync(string workspaceId);

    /// <summary>
    /// 构建工作区知识上下文文本（全量注入 system prompt，受 token 预算限制）
    /// </summary>
    /// <param name="workspaceId">工作区 ID</param>
    /// <param name="tokenBudget">token 预算上限</param>
    /// <returns>拼合后的知识文本；无内容时返回 null</returns>
    string? BuildWorkspaceKnowledgeContext(string workspaceId, string? knowledgeFilePath, int tokenBudget);

    /// <summary>
    /// 写入后检查单个工作区知识文件；超过当前预算时使用次级模型压缩并覆盖该文件。
    /// 次级模型不可用或压缩失败时保留原文件。
    /// </summary>
    Task EnforceKnowledgeFileBudgetAsync(string fullPath, CancellationToken ct = default);
}
