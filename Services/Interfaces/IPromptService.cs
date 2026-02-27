using Athena.UI.Models;
using System;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// Prompt 服务接口
/// </summary>
public interface IPromptService
{
    /// <summary>
    /// 获取指定类型的 Prompt
    /// </summary>
    string GetPrompt(PromptType type);

    /// <summary>
    /// 获取格式化的主动消息 Prompt
    /// </summary>
    string GetProactiveMessagePrompt(string intent, DateTime currentTime);

    /// <summary>
    /// 重新加载 Prompt（从外部文件）
    /// </summary>
    Task ReloadAsync();

    /// <summary>
    /// 事件：Prompt 被更新
    /// </summary>
    event EventHandler<PromptType>? PromptUpdated;
}
