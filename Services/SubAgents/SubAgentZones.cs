using Athena.UI.Models;
using Athena.UI.Services.GameMode;

namespace Athena.UI.Services.SubAgents;

/// <summary>
/// 工具名 → 猫头鹰所在"场所"。不另存一张表：从游戏模式的工具类别表（<see cref="PolisToolCategories"/>）派生，
/// 新工具在那里登记一次，城邦和猫头鹰村就都有动作（设计稿 6.1）。未知工具归入工坊。
/// </summary>
public static class SubAgentZones
{
    public static SubAgentZone ForTool(string functionName) => PolisToolCategories.ForTool(functionName) switch
    {
        PolisActionCategory.Read or PolisActionCategory.Write => SubAgentZone.Files,
        PolisActionCategory.Web => SubAgentZone.Web,
        PolisActionCategory.Memory => SubAgentZone.Library,
        // 终端、派发子代理、配置 / 定时任务 / 图像 / 技能 / MCP：都在工坊里干
        _ => SubAgentZone.Workshop
    };
}
