using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Athena.UI.Services.GameMode;

/// <summary>
/// 合成夹具：一棵内存里的目录树和一段编出来的三回合会话，走和真实数据完全相同的管线
/// （扫描 → 汇总 → 账本 → 回放转换 → 组合）。测试、冒烟和演示的默认场景都用它；由真实数据导出的夹具不进仓库。
/// 覆盖面：六类建筑都有、并发的一批读、城外路径、失败、侍女、记忆、估出来的收尾、被剪掉的空闲。
/// </summary>
public static class PolisSyntheticFixture
{
    public static DateTimeOffset Now { get; } = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    public const string WorkspaceRoot = "/Users/demo/我的工作区";
    public const string Home = "/Users/demo";
    public const string WorkspaceName = "我的工作区";

    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 9, 0, 0, TimeSpan.FromHours(8));

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

    public static IReadOnlyList<PolisReplayMessage> Conversation()
    {
        var messages = new List<PolisReplayMessage>();
        var call = 0;
        string P(string relative) => WorkspaceRoot + "/" + relative;

        void User(double at) => messages.Add(new PolisReplayMessage("user", T0.AddSeconds(at), false, null, null, null, null, 0));

        void Round(double at, int reasoning, params (string Name, object Args, double DoneAt, bool Ok)[] calls)
        {
            var ids = calls.Select(_ => "call_" + (++call).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var json = JsonSerializer.Serialize(calls.Select((c, i) => new
            {
                Id = ids[i],
                FunctionName = c.Name,
                Arguments = JsonSerializer.Serialize(c.Args)
            }));
            messages.Add(new PolisReplayMessage("assistant", T0.AddSeconds(at), true, json, null, null, null, reasoning));
            for (var i = 0; i < calls.Length; i++)
            {
                messages.Add(new PolisReplayMessage("tool", T0.AddSeconds(calls[i].DoneAt), false, null, ids[i], calls[i].Ok, null, 0));
            }
        }

        void Bubble(double at, long? durationMs) => messages.Add(new PolisReplayMessage("assistant", T0.AddSeconds(at), false, null, null, null, durationMs, 0));

        // 第一回合：去书库读合同，一批并发读两件，再写一份摘要
        User(0);
        Round(6.2, 820, ("read_system_file", new { path = P("合同/租赁合同.docx") }, 6.26, true));
        Round(10.9, 400,
            ("read_system_file", new { path = P("合同/附件/附件一.pdf") }, 11.02, true),
            ("list_system_directory", new { path = P("合同") }, 11.03, true));
        Round(15.5, 0, ("write_system_file", new { path = P("合同/合同摘要.md"), content = "（正文不进回放）" }, 15.58, true));
        Bubble(0.05, 21_000);

        // 第二回合：锻炉、港口、作坊。距上一回合交付 39 秒，回放里剪成 3 秒
        User(60);
        Round(64.1, 0, ("execute_terminal_command", new { command = "python3", arguments = new[] { "统计.py" }, workingDirectory = P("网站") }, 66.4, true));
        Round(69.0, 300, ("web_search", new { query = "（查询词不进回放）" }, 71.5, true));
        Round(74.2, 0, ("fetch_url_to_file", new { url = "https://example.invalid/q.pdf", outputPath = P("下载/报价单.pdf") }, 76.0, true));
        Round(79.0, 150, ("modify_system_file", new { path = P("网站/app.js"), diffContent = "…" }, 79.1, true));
        Bubble(60.05, 24_500);

        // 第三回合：图书馆、黄金侍女、城外（失败）、~ 开头的路径；气泡没有总时长，收尾按估计
        User(100);
        Round(103.0, 0, ("recall_from_memory", new { query = "上次的预算" }, 103.3, true));
        Round(107.5, 600, ("dispatch_subagents", new { tasks = new[] { new { title = "甲" }, new { title = "乙" }, new { title = "丙" } } }, 119.5, true));
        Round(123.0, 0, ("read_system_file", new { path = "/etc/hosts" }, 123.02, false));
        Round(127.5, 0, ("read_system_file", new { path = "~/我的工作区/账本/2026预算.xlsx" }, 127.6, true));
        Bubble(100.05, null);

        return messages;
    }

    /// <summary>用真实管线生成合成夹具。</summary>
    public static PolisFixtureDocument Build()
    {
        var scan = PolisScanner.Scan(Enumerator());
        var summary = PolisBuildings.Summarize(scan, Now);
        var layout = PolisLayoutLedger.Update(PolisLedger.Empty, summary.Buildings);
        var replay = PolisReplayConverter.Convert(Conversation(), WorkspaceRoot, Home);
        return PolisFixture.Compose(WorkspaceName, scan, summary, layout, replay, Now, synthetic: true);
    }
}
