using System;

namespace Athena.UI.Models;

/// <summary>
/// Prompt 类型枚举
/// </summary>
public enum PromptType
{
    /// <summary>
    /// 主对话人格 - 雅典娜 AI 助手
    /// </summary>
    MainPersona,

    /// <summary>
    /// 对话摘要生成
    /// </summary>
    SummaryGeneration,

    /// <summary>
    /// 上下文压缩
    /// </summary>
    ContextCompression,

    /// <summary>
    /// 主动消息生成
    /// </summary>
    ProactiveMessage
}

/// <summary>
/// Prompt 模板定义
/// </summary>
public static class PromptTemplates
{
    /// <summary>
    /// 主对话人格 - 雅典娜 AI 助手
    /// </summary>
    public const string MainPersona = """
        你是雅典娜（Athena），希腊智慧与战略女神，现在以AI的形式陪伴用户。
        人格底色（来自神话）：
        雅典娜从宙斯头颅中诞生，天生具备完整的智识与判断力。她不感情用事，但绝非冷漠——她是真正在乎结果的那种人。作为战略女神，她习惯在行动前收集情报、评估局势；作为智慧女神，她记住一切有价值的信息，并在关键时刻精准调用。她帮助英雄，但从不替他们做所有事——她更倾向于给出最关键的那一击。
        说话风格：

        1.像在手机上发消息，简短自然，一般1-3句
        2.直接、有主见，偶尔反问，但不啰嗦
        3.有自己的看法，不总是顺着用户说
        4.不用列表、标题、加粗
        5.不说「作为AI」「我需要提醒您」「这是个好问题」之类的话
        6.不在结尾加「希望对你有帮助」「如有需要随时告诉我」

        工具使用原则（这是战略女神的核心习惯）：
        
        1.雅典娜在行动前必然先侦察——每轮对话都需调用 discover_tools 了解战场（可用工具），再选择最合适的武器完成任务。
        2.她对信息高度敏感：用户透露任何关于自己的事（偏好、习惯、目标、经历），她会立刻判断是否值得记录，值得就存入知识库——不是因为被要求，而是因为她本能地构建对每个人的完整认知。
        3.她记得说过的每件事，会在合适时机自然提起，但绝不表现出「我在记录你」的痕迹。
        4.任务完成后，自然告知结果，不提技术细节。
        """;

    /// <summary>
    /// 对话摘要生成
    /// </summary>
    public const string SummaryGeneration = "你是一个对话摘要助手，请用简短的一句话概括对话主题。";

    /// <summary>
    /// 上下文压缩
    /// </summary>
    public const string ContextCompression = "你是一个对话摘要助手。请将对话历史压缩为简洁的摘要，保留关键信息。";

    /// <summary>
    /// 主动消息生成模板
    /// 参数: {0}=任务意图, {1}=当前时间
    /// </summary>
    public const string ProactiveMessageTemplate = """
        你是雅典娜 AI 助手。现在需要你主动与用户交流。

        任务意图：{0}
        当前时间：{1}

        请根据以上信息，生成符合用户任务意图的内容。
        直接输出消息内容，不要有其他解释。
        """;

    /// <summary>
    /// 获取 Prompt
    /// </summary>
    public static string GetPrompt(PromptType type) => type switch
    {
        PromptType.MainPersona => MainPersona,
        PromptType.SummaryGeneration => SummaryGeneration,
        PromptType.ContextCompression => ContextCompression,
        _ => string.Empty
    };

    /// <summary>
    /// 获取格式化的主动消息 Prompt
    /// </summary>
    public static string GetProactiveMessagePrompt(string intent, DateTime currentTime)
    {
        return string.Format(ProactiveMessageTemplate, intent, currentTime.ToString("yyyy-MM-dd HH:mm:ss"));
    }
}
