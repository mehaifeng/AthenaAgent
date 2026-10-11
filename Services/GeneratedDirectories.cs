using System;
using System.Collections.Generic;

namespace Athena.UI.Services;

/// <summary>
/// 构建/依赖产物目录的名单。目录搜索（<see cref="FileSystemService"/>）和游戏模式的城邦扫描共用这一份：
/// 两边各写一份，迟早有一边会漏掉新加的名字。
/// </summary>
public static class GeneratedDirectories
{
    /// <summary>构建/依赖产物目录：搜索它们几乎总是噪音，且能让一次搜索慢上两个数量级。</summary>
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", "dist", "build",
        "__pycache__", ".venv", "venv", "packages", ".gradle", "target", ".next", ".nuxt"
    };

    /// <summary>目录名（不含路径）是否属于构建/依赖产物。大小写不敏感。</summary>
    public static bool IsGenerated(string directoryName) => Names.Contains(directoryName);
}
