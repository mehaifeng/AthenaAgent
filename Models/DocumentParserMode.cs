namespace Athena.UI.Models;

/// <summary>
/// MinerU 文档解析方式
/// </summary>
public enum DocumentParserMode
{
    /// <summary>
    /// 极速解析 API（Agent Lightweight）：无需登录、无需 Token，按 IP 限流。
    /// </summary>
    AgentLightweight = 0,

    /// <summary>
    /// 精度解析 API（Precision）：需要 Token，支持更大文件与更高精度。
    /// </summary>
    Precision = 1
}
