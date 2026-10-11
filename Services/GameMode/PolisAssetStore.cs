using Serilog;
using System;
using System.IO;

namespace Athena.UI.Services.GameMode;

/// <summary>回环服务向页面提供外部模型文件时问的那一半：一段文件名 → 资产目录里的真实路径（不在就是 null）。</summary>
public interface IPolisAssetFiles
{
    /// <summary>
    /// 页面要的一个文件（模型、缓冲、贴图）在不在资产目录里：名字必须是一段干净的文件名，
    /// 文件字面上与解析软链之后都在资产目录之内。不在就返回 false——回环服务回 404，页面退回程序化的那一件。
    /// </summary>
    bool TryResolveFile(string? name, out string fullPath);
}

/// <summary>
/// 外部资产（设计稿第 1 节"保留资产清单接口"）：<c>AthenaData/Game/assets/</c> 下一份 <c>manifest.json</c> 和它引用的 glTF。
/// 这里只做文件那一层——读清单（有大小上限）、按 <see cref="PolisAssetManifest"/> 校验、按名字找文件；
/// 资产目录是用户放东西的地方，所以每一个名字都当作不可信：只认一段文件名，解析软链之后也必须还在目录里
/// （与文件工具同一个 realpath 实现），目录本身不存在就是"没有外部资产"。
/// </summary>
public sealed class PolisAssetStore : IPolisAssetFiles
{
    private readonly ILogger _logger;

    public PolisAssetStore(string directory, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
        _logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<PolisAssetStore>();
    }

    /// <summary>资产目录（不一定存在）。</summary>
    public string Directory { get; }

    /// <summary>单个外部文件的上限：一个 glTF 不该比这更大，回环服务也不该一次读进来更多。</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    /// <summary>
    /// 读清单并校验。没有清单、读不了、写错了：结果里没有覆盖，每一条被拒的原因写一条 Warning——
    /// 页面对它们照常用程序化的那一件（缺省回退）。
    /// </summary>
    public PolisAssetManifestResult LoadManifest()
    {
        var path = Path.Combine(Directory, PolisAssetManifest.FileName);
        string? json = null;
        try
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                json = info.Length > PolisAssetManifest.MaxManifestBytes
                    ? new string(' ', PolisAssetManifest.MaxManifestBytes + 1)   // 交给 Parse 报"太大"
                    : File.ReadAllText(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(ex, "Polis asset manifest {Path} could not be read; every asset stays procedural", path);
            return new PolisAssetManifestResult(new System.Collections.Generic.Dictionary<string, PolisAssetOverride>(), new[] { "manifest could not be read" });
        }
        var result = PolisAssetManifest.Parse(json, name => TryResolveFile(name, out _));
        foreach (var rejection in result.Rejections)
            _logger.Warning("Polis asset manifest {Path}: {Rejection}", path, rejection);
        if (result.Assets.Count > 0)
            _logger.Information("Polis asset manifest {Path}: {Count} asset(s) replaced by external models", path, result.Assets.Count);
        return result;
    }

    public bool TryResolveFile(string? name, out string fullPath)
    {
        fullPath = string.Empty;
        if (!PolisAssetManifest.IsServableFileName(name)) return false;
        if (!System.IO.Directory.Exists(Directory)) return false;
        var candidate = Path.GetFullPath(Path.Combine(Directory, name!));
        var comparison = PolisPaths.PlatformComparison;
        if (PolisPaths.ToRelative(Directory, candidate, comparison) is not { Length: > 0 } relative || relative.Contains('/')) return false;
        if (!File.Exists(candidate)) return false;
        if (!FileSystemService.TryResolveFully(Directory, out var realRoot) || !FileSystemService.TryResolveFully(candidate, out var realFile)) return false;
        if (PolisPaths.ToRelative(realRoot, realFile, comparison) is not { Length: > 0 })
        {
            _logger.Warning("Polis asset {Name} resolves through a link to a place outside {Directory}; refused", name, Directory);
            return false;
        }
        try
        {
            if (new FileInfo(realFile).Length > MaxFileBytes) return false;
        }
        catch (IOException)
        {
            // 文件在核对的这一刻被删了或被独占：当它不在，页面退回程序化的那一件
            return false;
        }
        fullPath = realFile;
        return true;
    }
}
