using System;
using System.Collections.Generic;

namespace Athena.UI.Services.SubAgents;

public enum ToolGateCategory
{
    None,
    Write
}

/// <summary>
/// 并发子代理的工具闸分类，决定某个工具执行时是否需要串行、走哪把闸：
/// - Write：会改写共享状态的工具（文件系统写、知识库向量库写）串行，规避并发竞态。
/// - None：只读 / 网络 / 快速类工具，自由并行（这是子代理并行的价值所在）。
/// </summary>
public static class SubAgentToolGates
{
    private static readonly HashSet<string> WriteTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "create_new_memory",
        "write_system_file", "modify_system_file", "delete_system_file",
        "move_system_file", "copy_system_file", "create_directory"
    };

    public static ToolGateCategory Category(string functionName)
    {
        if (string.IsNullOrEmpty(functionName)) return ToolGateCategory.None;
        if (WriteTools.Contains(functionName)) return ToolGateCategory.Write;
        return ToolGateCategory.None;
    }
}
