using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Athena.UI.Services.GameMode;

/// <summary>一处改动是谁做的（设计稿 11.2）。只有雅典娜的改动配角色动作和光；外部改动只让建筑轻微变化。</summary>
public enum PolisChangeOrigin
{
    Athena,
    External
}

/// <summary>
/// 雅典娜在这一回合碰过的地方：一次工具调用的目标（文件或目录），以及它起止的时间。
/// <see cref="RelativePath"/> 为 null 且 <see cref="WholeWorkspace"/> 为 true 时覆盖整个工作区——
/// 派发子代理期间，黄金侍女是替她跑腿的，她们在城里做的事也算她的。
/// </summary>
public sealed record PolisAthenaTouch(
    string? RelativePath,
    DateTimeOffset Start,
    DateTimeOffset? End,
    bool WholeWorkspace = false);

/// <summary>监听报来的一处改动，已换算成工作区相对路径。改名时 <see cref="OldRelativePath"/> 有值。</summary>
public sealed record PolisFsChange(
    string RelativePath,
    WatcherChangeTypes Type,
    string? OldRelativePath,
    DateTimeOffset At);

/// <summary>
/// 一批改动合并之后要做的事。<see cref="DirtyTopLevelNames"/> 是受影响的顶层名字：深处的改动记到它所在的顶层文件夹，
/// 根目录下直接增删的一项记它自己的名字——它可能是一座建筑（新建 / 删除的文件夹），也可能是广场上的一个文件，
/// 由调用方看磁盘决定；这种情况同时置 <see cref="AgoraDirty"/>。
/// </summary>
public sealed record PolisChangeBatch(
    bool FullRescan,
    IReadOnlySet<string> DirtyTopLevelNames,
    bool AgoraDirty,
    IReadOnlyList<(string From, string To)> TopLevelRenames,
    IReadOnlySet<string> AthenaBuildings,
    IReadOnlySet<string> ExternalBuildings,
    int Changes)
{
    public bool IsEmpty => !FullRescan && DirtyTopLevelNames.Count == 0 && !AgoraDirty && TopLevelRenames.Count == 0;
}

/// <summary>
/// 运行期的外部改动同步（11.2）。纯函数：归属判断与批量合并。监听只是提示——这里决定的是"哪几座建筑要重算"
/// 和"要不要整城重扫"，重算本身仍然靠扫描。
/// </summary>
public static class PolisChanges
{
    /// <summary>
    /// 工具结束之后还认它多久：写盘的通知（FSEvents 会攒约 1 秒）常常晚于工具的"执行完毕"到达。
    /// </summary>
    public static readonly TimeSpan AthenaGrace = TimeSpan.FromSeconds(5);

    /// <summary>开始之前也让一点：监视器与工具记录的时间来自不同的时钟读数。</summary>
    public static readonly TimeSpan AthenaLeadIn = TimeSpan.FromSeconds(1);

    /// <summary>一批里改动超过这个数（git checkout、解压一个包），就不逐座重算，播一次"城里起了大变化"并整城重扫。</summary>
    public const int FullRescanChangeThreshold = 200;

    /// <summary>一批里涉及的建筑超过这个数，同样整城重扫：逐座重算已经不比整城便宜。</summary>
    public const int FullRescanBuildingThreshold = 8;

    /// <summary>
    /// 这处改动是不是雅典娜做的：路径与她这一回合某次工具调用的目标一致（或落在那个目录之下），
    /// 而且时间落在那次调用的窗口里（开始前 <see cref="AthenaLeadIn"/> 到结束后 <see cref="AthenaGrace"/>）。
    /// 对不上的一律算外部改动——"光属于雅典娜"这条规则不能撒谎（设计稿第 8 节）。
    /// </summary>
    public static PolisChangeOrigin Attribute(
        string relativePath,
        DateTimeOffset at,
        IReadOnlyList<PolisAthenaTouch> touches,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(touches);
        foreach (var touch in touches)
        {
            var windowEnd = (touch.End ?? now) + AthenaGrace;
            if (at < touch.Start - AthenaLeadIn || at > windowEnd) continue;
            if (touch.WholeWorkspace) return PolisChangeOrigin.Athena;
            if (touch.RelativePath == null) continue;
            if (touch.RelativePath.Length == 0) return PolisChangeOrigin.Athena;   // 目标就是工作区根目录
            if (string.Equals(relativePath, touch.RelativePath, StringComparison.Ordinal)
                || relativePath.StartsWith(touch.RelativePath + "/", StringComparison.Ordinal))
                return PolisChangeOrigin.Athena;
        }
        return PolisChangeOrigin.External;
    }

    /// <summary>
    /// 合并一批改动（去抖窗口内攒下的）：按建筑归组；隐藏项与构建 / 依赖目录里的改动不进城（与扫描同一套规则）；
    /// 顶层文件夹的改名单独列出（建筑原地换牌匾）；超过阈值就整城重扫。
    /// <paramref name="origins"/> 与 <paramref name="changes"/> 一一对应。
    /// </summary>
    public static PolisChangeBatch Group(IReadOnlyList<PolisFsChange> changes, IReadOnlyList<PolisChangeOrigin> origins)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(origins);
        if (origins.Count != changes.Count) throw new ArgumentException("Every change needs an origin.", nameof(origins));

        var dirty = new HashSet<string>(StringComparer.Ordinal);
        var athena = new HashSet<string>(StringComparer.Ordinal);
        var external = new HashSet<string>(StringComparer.Ordinal);
        var renames = new List<(string, string)>();
        var agora = false;
        var counted = 0;

        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            var paths = change.OldRelativePath == null
                ? new[] { change.RelativePath }
                : new[] { change.OldRelativePath, change.RelativePath };
            var relevant = paths.Where(p => p.Length > 0 && PolisPaths.IsCleanRelativePath(p) && !PolisPaths.IsOutsideSurvey(p)).ToList();
            if (relevant.Count == 0) continue;
            counted++;

            if (change.Type == WatcherChangeTypes.Renamed
                && change.OldRelativePath != null
                && !change.OldRelativePath.Contains('/')
                && !change.RelativePath.Contains('/')
                && relevant.Count == 2)
            {
                // 顶层一项改名：可能是文件夹（换牌匾），也可能是根目录下的文件（广场）。由调用方按磁盘判断。
                renames.Add((change.OldRelativePath, change.RelativePath));
            }

            foreach (var path in relevant)
            {
                var top = PolisPaths.TopLevelOf(path);
                if (top.Length == 0) agora = true;
                var name = top.Length > 0 ? top : path;
                dirty.Add(name);
                (origins[i] == PolisChangeOrigin.Athena ? athena : external).Add(name);
            }
        }

        var full = counted > FullRescanChangeThreshold || dirty.Count > FullRescanBuildingThreshold;
        return new PolisChangeBatch(full, dirty, agora, renames, athena, external, counted);
    }
}
