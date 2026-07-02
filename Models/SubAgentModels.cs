using System.Text.Json.Serialization;

namespace Athena.UI.Models;

/// <summary>
/// 子代理模型来源。InheritMain：复用主 AI 的 Provider/BaseUrl/ApiKey/Model；
/// Custom：使用独立填写的子代理模型字段（留空时逐字段回退主 AI）。仿浏览器智能体。
/// </summary>
public enum SubAgentModelSource
{
    InheritMain,
    Custom
}

/// <summary>子代理（猫头鹰）当前所在的"场所"，由它正在调用的工具类别决定。</summary>
public enum SubAgentZone
{
    Meditation,  // 冥想区：无工具运行（思考 / 等待回应）——默认的家
    Files,       // 文件区
    Web,         // 电脑区：网页搜索 / 浏览器
    Library,     // 书房区：知识库记忆
    Workshop,    // 工坊：CLI / 生图 / 配置 / 任务
    Perch        // 归巢栖木：已完成
}

/// <summary>子代理生命周期状态。</summary>
public enum SubAgentState
{
    Pending,
    Running,
    Done,
    Error,
    Cancelled
}

/// <summary>派给单个子代理的任务（dispatch_subagents 的 tasks 数组项）。</summary>
public sealed class SubAgentTaskInput
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("agent_type")]
    public string AgentType { get; set; } = "general";

    [JsonPropertyName("instruction")]
    public string Instruction { get; set; } = string.Empty;
}

/// <summary>单个子代理运行结束后的结果，供编排器合并回传主上下文。</summary>
public sealed class SubAgentResult
{
    public string Title { get; init; } = string.Empty;
    public string AgentType { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string Summary { get; init; } = string.Empty;
}
