using Athena.UI.Models;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 模型角色：统一凭据出口按角色解析（订阅计划 Phase 1）。
/// 每个角色对应一条凭据消费路径，所有分叉逻辑收敛到 <see cref="IModelEndpointResolver"/> 一处，
/// 便于 Phase 3 在 Custom / Subscription 两种模式之间无损切换。
/// </summary>
public enum ModelRole
{
    /// <summary>主模型（聊天/工具执行）。</summary>
    Primary,

    /// <summary>次级模型（摘要/压缩/历史标题）。</summary>
    Secondary,

    /// <summary>嵌入模型（知识库向量）。</summary>
    Embedding,

    /// <summary>图像生成模型。</summary>
    Image,

    /// <summary>语音合成（TTS）。特例：BaseUrl 是完整 <c>.../audio/speech</c> 端点。</summary>
    Audio,

    /// <summary>浏览器智能体视觉模型。</summary>
    BrowserAgent,

    /// <summary>子代理模型。</summary>
    SubAgent,

    /// <summary>知识库整理 Agent 模型。</summary>
    Maintenance
}

/// <summary>
/// 统一凭据出口：把「按角色解析 (Provider/BaseUrl/ApiKey/Model)」这一唯一会随模式变化的关注点收敛到一处。
/// 采样参数（温度、token 预算、语音、自动播放）不随模式变化，仍由各调用方读取配置，不进本接口。
/// <para>
/// Custom 模式实现 <see cref="Services.CustomModelEndpointResolver"/> 保持既有 BYOK 继承树语义（行为不变）；
/// Phase 3 会新增 Subscription 实现与组合根按 <c>ModelAccessMode</c> 分发。
/// </para>
/// </summary>
public interface IModelEndpointResolver
{
    /// <summary>
    /// 解析指定角色的有效凭据与模型名。
    /// Audio 特例：返回的 BaseUrl 是完整音频端点，语义与既有 <see cref="Services.AudioConfigResolver"/> 一致。
    /// </summary>
    EffectiveModelConfig Resolve(ModelRole role, AppConfig config);
}
