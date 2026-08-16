namespace Athena.UI.Models;

/// <summary>
/// Office（OOXML）工具集的披露策略。
///
/// 这 15 个工具（xlsx 6 + docx 5 + pptx 4）的 JSON Schema 声明约占整个工具列表 35% 的体量，
/// 而绝大多数会话从头到尾不碰一个 Office 文件——那部分是每一轮请求都在白付的上下文税。
///
/// 追加新档位必须放在末尾：配置按枚举数字序列化，插入中间会改变历史值的语义。
/// </summary>
public enum OfficeToolsMode
{
    /// <summary>默认：只暴露 enable_office_tools 一个入口，模型用得上时自行解锁（按对话隔离）。</summary>
    Auto,

    /// <summary>始终暴露全部 Office 工具：省掉一次解锁往返，代价是每轮都带上全部声明。</summary>
    Always,

    /// <summary>完全隐藏，连解锁入口也不暴露。</summary>
    Off
}
