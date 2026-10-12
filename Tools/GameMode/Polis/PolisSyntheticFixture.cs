using System;
using System.Collections.Generic;

namespace Athena.UI.Services.GameMode;

/// <summary>
/// 合成夹具：一棵内存里的目录树，走和真实数据完全相同的管线（扫描 → 汇总 → 账本 → 组合）。
/// 测试、冒烟和演示的默认场景都用它；由真实数据导出的夹具不进仓库。覆盖面：六类建筑都有、市集、空地、被跳过的目录。
/// （编出来的三回合会话连同回放一起拿掉了，2026-10-13：雅典娜不再镜像回合。）
/// </summary>
public static class PolisSyntheticFixture
{
    public static DateTimeOffset Now { get; } = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    public const string WorkspaceRoot = "/Users/demo/我的工作区";
    public const string Home = "/Users/demo";
    public const string WorkspaceName = "我的工作区";

    public static IReadOnlyDictionary<string, IReadOnlyList<PolisFsEntry>> Tree()
    {
        static PolisFsEntry F(string name, long size, double ageDays) => new(name, false, size, Now.AddDays(-ageDays));
        static PolisFsEntry D(string name, double ageDays) => new(name, true, 0, Now.AddDays(-ageDays));

        return new Dictionary<string, IReadOnlyList<PolisFsEntry>>(StringComparer.Ordinal)
        {
            [""] = new[]
            {
                F("README.md", 2_048, 1), F("待办.txt", 300, 0.12), F(".DS_Store", 6_148, 0.1),
                D("合同", 0.1), D("照片", 20), D("账本", 6), D("网站", 0.05), D("下载", 40), D("杂物", 100),
                D("旧项目", 800), D("空文件夹", 400), D("node_modules", 1), D(".git", 0.01),
            },
            ["合同"] = new[] { F("租赁合同.docx", 48_000, 0.1), F("采购合同.docx", 52_000, 3), F("保密协议.pdf", 210_000, 30), F("说明.md", 900, 1), D("附件", 2) },
            ["合同/附件"] = new[] { F("附件一.pdf", 120_000, 2), F("附件二.pdf", 98_000, 2) },
            ["照片"] = new[] { F("海边.jpg", 3_200_000, 20), F("生日.jpg", 2_900_000, 25), F("毕业照.png", 4_100_000, 60), F("相册说明.txt", 400, 20), D("2025", 200) },
            ["照片/2025"] = new[] { F("01.jpg", 2_000_000, 200), F("02.jpg", 2_100_000, 200), F("03.jpg", 1_900_000, 201) },
            ["账本"] = new[] { F("2026预算.xlsx", 64_000, 6), F("流水.csv", 12_000, 7), F("月度汇总.xlsx", 70_000, 9) },
            ["网站"] = new[] { F("app.js", 9_000, 0.05), F("style.css", 3_000, 2), F("server.py", 5_000, 4), F("package.json", 700, 10), F("README.md", 1_200, 10) },
            ["下载"] = new[] { F("安装包.zip", 80_000_000, 40), F("资料.tar.gz", 12_000_000, 41), F("旧备份.7z", 300_000_000, 90), F("说明.pdf", 400_000, 40) },
            ["杂物"] = new[] { F("歌.mp3", 6_000_000, 100), F("草稿.md", 2_000, 120), F("截图.png", 900_000, 150), F("表.csv", 1_000, 180) },
            ["旧项目"] = new[] { F("2019总结.docx", 40_000, 800), F("方案.pptx", 2_000_000, 820), F("数据.xlsx", 30_000, 900) },
            ["空文件夹"] = Array.Empty<PolisFsEntry>(),
            ["node_modules"] = new[] { F("never-read.js", 1, 1) },
            [".git"] = new[] { F("HEAD", 1, 1) },
        };
    }

    public static Func<string, IReadOnlyList<PolisFsEntry>> Enumerator()
    {
        var tree = Tree();
        return dir => tree.TryGetValue(dir, out var entries)
            ? entries
            : throw new System.IO.DirectoryNotFoundException(dir);
    }

    /// <summary>用真实管线生成合成夹具。</summary>
    public static PolisFixtureDocument Build()
    {
        var scan = PolisScanner.Scan(Enumerator());
        var summary = PolisBuildings.Summarize(scan, Now);
        var layout = PolisLayoutLedger.Update(PolisLedger.Empty, summary.Buildings);
        return PolisFixture.Compose(WorkspaceName, scan, summary, layout, Now, synthetic: true);
    }
}
