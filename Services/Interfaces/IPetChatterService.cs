using Athena.UI.Models;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 宠物台词来源。两级：本地台词库永远可用、零成本、离线可用；
/// 模型台词是可选增强，任何一次失败/限流都静默退回本地台词，绝不让宠物"卡住不说话"。
/// </summary>
public interface IPetChatterService
{
    /// <summary>模型台词此刻是否可用（开关已开且 Companion/标题角色其一已配置）。</summary>
    bool IsModelChatterAvailable { get; }

    /// <summary>本地台词库。永远返回一句可显示的话。</summary>
    string GetLocalLine(PetChatterTopic topic, PetMoodBand band);

    /// <summary>
    /// 尝试用小模型生成一句台词。被限流、未配置或出错时返回 null，
    /// 调用方据此回退到 <see cref="GetLocalLine"/>。
    /// </summary>
    Task<string?> TryGenerateAsync(PetChatterRequest request, CancellationToken cancellationToken = default);
}
