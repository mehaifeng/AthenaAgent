using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Athena.UI.Services.GameMode;

/// <summary>文件按"里面放的是什么"分的类。决定建筑类型（设计稿 5.1）。</summary>
public enum PolisFileKind
{
    Document,
    Image,
    Sheet,
    Code,
    Archive,
    Other
}

/// <summary>顶层文件夹变成的建筑类型（设计稿 5.1）。</summary>
public enum PolisBuildingKind
{
    /// <summary>柱廊书库：主要放文档。</summary>
    StoaLibrary,
    /// <summary>雕塑园：主要放图片。</summary>
    SculptureGarden,
    /// <summary>金库：主要放表格。（市集留给顶层文件夹太多时的摊位，见 <see cref="PolisLayoutLedger"/>。）</summary>
    Treasury,
    /// <summary>作坊：主要放代码。</summary>
    Workshop,
    /// <summary>仓库：主要放压缩包。</summary>
    Warehouse,
    /// <summary>民居：混杂，或者是空的。</summary>
    House
}

/// <summary>建筑状态，由最近修改时间决定：常用的有人走动、有灯光，很久没动的爬满常春藤，慢慢变成废墟。</summary>
public enum PolisBuildingState
{
    Bustling,
    Lived,
    Quiet,
    Ivy,
    Ruin
}

public sealed record PolisBuilding(
    string Key,
    PolisBuildingKind Kind,
    int FileCount,
    long TotalBytes,
    IReadOnlyDictionary<PolisFileKind, int> KindCounts,
    DateTimeOffset LastModifiedUtc,
    PolisBuildingState State,
    int SizeClass,
    bool Incomplete);

/// <summary>根目录下直接放着的文件：它们不成建筑，归广场。</summary>
public sealed record PolisAgoraSummary(int FileCount, long TotalBytes, DateTimeOffset? LastModifiedUtc, bool Incomplete);

public sealed record PolisSummary(IReadOnlyList<PolisBuilding> Buildings, PolisAgoraSummary Agora);

/// <summary>建筑汇总：扫描结果 → 每个顶层文件夹的类型、体量、状态。纯函数，"现在"由调用方给。</summary>
public static class PolisBuildings
{
    /// <summary>占全部文件的比例达到这个值，才算"主要放"某一类；否则是民居。</summary>
    public const double DominantShare = 0.5;

    private static readonly Dictionary<string, PolisFileKind> KindByExtension = BuildKindTable();

    private static Dictionary<string, PolisFileKind> BuildKindTable()
    {
        var table = new Dictionary<string, PolisFileKind>(StringComparer.OrdinalIgnoreCase);
        void Add(PolisFileKind kind, params string[] extensions)
        {
            foreach (var ext in extensions) table.Add(ext, kind);
        }

        Add(PolisFileKind.Document,
            ".md", ".markdown", ".txt", ".rtf", ".pdf", ".doc", ".docx", ".odt", ".pages", ".tex", ".rst",
            ".epub", ".html", ".htm", ".org", ".adoc", ".ppt", ".pptx", ".odp", ".key");
        Add(PolisFileKind.Image,
            ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".tif", ".tiff", ".heic", ".heif",
            ".ico", ".icns", ".psd", ".ai", ".avif", ".raw", ".cr2", ".nef", ".dng");
        Add(PolisFileKind.Sheet, ".xlsx", ".xls", ".xlsm", ".csv", ".tsv", ".ods", ".numbers");
        Add(PolisFileKind.Code,
            ".cs", ".axaml", ".xaml", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".py", ".ps1", ".psm1",
            ".sh", ".bash", ".zsh", ".bat", ".cmd", ".java", ".kt", ".kts", ".go", ".rs", ".c", ".h", ".cpp",
            ".hpp", ".cc", ".m", ".mm", ".swift", ".rb", ".php", ".lua", ".sql", ".css", ".scss", ".less",
            ".vue", ".svelte", ".json", ".yaml", ".yml", ".toml", ".xml", ".csproj", ".sln", ".props",
            ".targets", ".gradle", ".cmake", ".iss", ".glsl", ".wgsl", ".r", ".scala", ".dart", ".ex",
            ".exs", ".erl", ".hs", ".fs", ".fsx", ".vb", ".pl", ".ipynb", ".editorconfig");
        Add(PolisFileKind.Archive,
            ".zip", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".7z", ".rar", ".zst", ".dmg", ".iso", ".pkg",
            ".cab", ".jar", ".nupkg");
        return table;
    }

    public static PolisFileKind ClassifyFile(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext.Length > 0 && KindByExtension.TryGetValue(ext, out var kind) ? kind : PolisFileKind.Other;
    }

    /// <summary>
    /// 建筑类型：某一类占全部文件（含"其他"）至少一半，就是那一类的建筑；否则、或者文件夹是空的，是民居。
    /// 两类恰好各占一半时按固定次序取（文档、代码、图片、表格、压缩包），结果与字典遍历顺序无关。
    /// </summary>
    public static PolisBuildingKind ChooseKind(IReadOnlyDictionary<PolisFileKind, int> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        var total = counts.Values.Sum();
        if (total == 0) return PolisBuildingKind.House;

        PolisFileKind[] priority = { PolisFileKind.Document, PolisFileKind.Code, PolisFileKind.Image, PolisFileKind.Sheet, PolisFileKind.Archive };
        var best = priority
            .Select(kind => (Kind: kind, Count: counts.TryGetValue(kind, out var n) ? n : 0))
            .OrderByDescending(x => x.Count)
            .First();   // OrderByDescending 是稳定排序：并列时保留 priority 的次序
        if (best.Count == 0 || best.Count < total * DominantShare) return PolisBuildingKind.House;

        return best.Kind switch
        {
            PolisFileKind.Document => PolisBuildingKind.StoaLibrary,
            PolisFileKind.Code => PolisBuildingKind.Workshop,
            PolisFileKind.Image => PolisBuildingKind.SculptureGarden,
            PolisFileKind.Sheet => PolisBuildingKind.Treasury,
            PolisFileKind.Archive => PolisBuildingKind.Warehouse,
            _ => PolisBuildingKind.House
        };
    }

    /// <summary>状态的年龄分界（含）：2 天、14 天、90 天、365 天。</summary>
    public static PolisBuildingState StateFor(DateTimeOffset lastModifiedUtc, DateTimeOffset nowUtc)
    {
        var age = nowUtc - lastModifiedUtc;
        if (age <= TimeSpan.FromDays(2)) return PolisBuildingState.Bustling;
        if (age <= TimeSpan.FromDays(14)) return PolisBuildingState.Lived;
        if (age <= TimeSpan.FromDays(90)) return PolisBuildingState.Quiet;
        if (age <= TimeSpan.FromDays(365)) return PolisBuildingState.Ivy;
        return PolisBuildingState.Ruin;
    }

    /// <summary>
    /// 体量分级取对数，免得一个大文件夹压垮全城：0–9 个文件 1 级，10–99 个 2 级，100–999 个 3 级，再多 4 级封顶。
    /// </summary>
    public static int SizeClassFor(int fileCount)
    {
        if (fileCount < 0) throw new ArgumentOutOfRangeException(nameof(fileCount));
        if (fileCount < 10) return 1;
        if (fileCount < 100) return 2;
        if (fileCount < 1000) return 3;
        return 4;
    }

    public static PolisSummary Summarize(PolisScanResult scan, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var byTop = scan.TopLevelDirectories.ToDictionary(
            d => d.Name,
            d => (Dir: d, Files: new List<PolisScannedFile>()),
            StringComparer.Ordinal);
        var agoraFiles = new List<PolisScannedFile>();

        foreach (var file in scan.Files)
        {
            var slash = file.RelativePath.IndexOf('/');
            if (slash < 0) agoraFiles.Add(file);
            else if (byTop.TryGetValue(file.RelativePath[..slash], out var bucket)) bucket.Files.Add(file);
        }

        var buildings = new List<PolisBuilding>(byTop.Count);
        foreach (var dir in scan.TopLevelDirectories)
        {
            var files = byTop[dir.Name].Files;
            var counts = new Dictionary<PolisFileKind, int>();
            foreach (var kind in Enum.GetValues<PolisFileKind>()) counts[kind] = 0;
            foreach (var file in files) counts[ClassifyFile(file.RelativePath)]++;

            // 没有文件的文件夹用它自己的修改时间
            var lastModified = files.Count == 0 ? dir.LastWriteUtc : files.Max(f => f.LastWriteUtc);
            buildings.Add(new PolisBuilding(
                dir.Name,
                ChooseKind(counts),
                files.Count,
                files.Sum(f => f.Size),
                counts,
                lastModified,
                StateFor(lastModified, nowUtc),
                SizeClassFor(files.Count),
                scan.IncompleteTopLevel.Contains(dir.Name)));
        }

        var agora = new PolisAgoraSummary(
            agoraFiles.Count,
            agoraFiles.Sum(f => f.Size),
            agoraFiles.Count == 0 ? null : agoraFiles.Max(f => f.LastWriteUtc),
            scan.RootIncomplete);
        return new PolisSummary(buildings, agora);
    }
}
