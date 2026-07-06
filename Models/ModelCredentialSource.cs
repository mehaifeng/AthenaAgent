namespace Athena.UI.Models;

/// <summary>
/// 模型角色的凭据来源（统一继承树）。
/// InheritMain：直接复用主 AI 的 Provider/BaseUrl/ApiKey（模型名仍取角色自身字段）；
/// Custom：使用角色独立填写的凭据字段（留空时逐字段回退主 AI）。
/// </summary>
public enum ModelCredentialSource
{
    InheritMain,
    Custom
}
