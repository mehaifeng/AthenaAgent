using System;
using System.Collections.Generic;

namespace Athena.UI.Services.SubAgents;

public enum ToolGateCategory
{
    None,
    Browser,
    Write
}

/// <summary>
/// 并发子代理的工具闸分类，决定某个工具执行时是否需要串行、走哪把闸：
/// - Browser：浏览器任务串行（单 Playwright 会话），仅浏览器任务之间排队，不阻塞其他工具。
/// - Write：会改写共享状态的工具（文件系统写、知识库向量库写）串行，规避并发竞态。
/// - None：只读 / 网络 / 快速类工具，自由并行（这是子代理并行的价值所在）。
///
/// 关键教训：不能用"一把全局闸串行所有工具"——那样一个 4 分钟的浏览器任务会把
/// 同批其他子代理的快速只读工具全部堵死，退化成串行。
/// </summary>
public static class SubAgentToolGates
{
    private static readonly HashSet<string> BrowserTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "run_browser_task"
    };

    private static readonly HashSet<string> WriteTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "create_new_memory",
        "write_system_file", "modify_system_file", "delete_system_file",
        "move_system_file", "copy_system_file", "create_directory"
    };

    public static ToolGateCategory Category(string functionName)
    {
        if (string.IsNullOrEmpty(functionName)) return ToolGateCategory.None;
        if (BrowserTools.Contains(functionName)) return ToolGateCategory.Browser;
        if (WriteTools.Contains(functionName)) return ToolGateCategory.Write;
        return ToolGateCategory.None;
    }
}
