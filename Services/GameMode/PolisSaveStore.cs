using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.GameMode;

/// <summary>
/// 存档放在哪一格：一座城邦（按工作区 Id）或雅典娜的神殿（全局对话，设计稿 9.1）。
/// </summary>
public readonly record struct PolisSaveSlot(string? WorkspaceId)
{
    public static PolisSaveSlot Sanctuary { get; } = new(null);

    public bool IsSanctuary => WorkspaceId == null;

    public static PolisSaveSlot ForWorkspace(string workspaceId)
    {
        if (!Guid.TryParse(workspaceId, out var parsed))
            throw new ArgumentException("Workspace ID must be a GUID.", nameof(workspaceId));
        return new PolisSaveSlot(parsed.ToString("N"));
    }

    public override string ToString() => WorkspaceId ?? "sanctuary";
}

public interface IPolisSaveStore
{
    Task<PolisSaveReadResult> LoadAsync(PolisSaveSlot slot, CancellationToken cancellationToken = default);

    Task SaveAsync(PolisSaveSlot slot, PolisSaveDocument document, CancellationToken cancellationToken = default);

    Task<PolisIndexDocument?> LoadIndexAsync(PolisSaveSlot slot, CancellationToken cancellationToken = default);

    Task SaveIndexAsync(PolisSaveSlot slot, PolisIndexDocument index, CancellationToken cancellationToken = default);
}

/// <summary>
/// 城邦存档的文件读写（设计稿 10.2）。每座城邦在 <c>AthenaData/Workspaces/&lt;id&gt;/game/</c> 下有两个文件：
/// <c>polis.json</c>（账本、藏品、玩家进度——丢了就是空间记忆和亲手摆的东西）与 <c>index.json</c>（纯缓存）。
/// 神殿在 <c>AthenaData/Game/sanctuary.json</c>。写法照搬 <c>CronTaskStore</c> / <c>PetProfileStore</c>：
/// 先写临时文件再原子替换；替换前把当前那份完好的副本留作 <c>.bak</c>；整份读不懂时改读 <c>.bak</c>，
/// 读不懂的原文另存一份 <c>.corrupt-*</c>，绝不悄悄覆盖；单条记录的隔离在 <see cref="PolisSaveFormat"/>。
/// 工作区目录随工作区一起删除（<c>WorkspaceService.DeleteAsync</c>），重新定位时原地保留。
/// </summary>
public sealed class PolisSaveStore : IPolisSaveStore
{
    public const string SaveFileName = "polis.json";
    public const string IndexFileName = "index.json";
    public const string SanctuaryFileName = "sanctuary.json";

    private readonly IPlatformPathService _paths;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public PolisSaveStore(IPlatformPathService paths, ILogger logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<PolisSaveStore>();
    }

    public string GetSavePath(PolisSaveSlot slot)
        => slot.IsSanctuary
            ? Path.Combine(_paths.GetAppDataDirectory(), "Game", SanctuaryFileName)
            : Path.Combine(GetCityDirectory(slot), SaveFileName);

    public string GetIndexPath(PolisSaveSlot slot)
        => slot.IsSanctuary
            ? Path.Combine(_paths.GetAppDataDirectory(), "Game", "sanctuary.index.json")
            : Path.Combine(GetCityDirectory(slot), IndexFileName);

    private string GetCityDirectory(PolisSaveSlot slot)
        => Path.Combine(_paths.GetWorkspacesDirectory(), PolisSaveSlot.ForWorkspace(slot.WorkspaceId!).WorkspaceId!, "game");

    public async Task<PolisSaveReadResult> LoadAsync(PolisSaveSlot slot, CancellationToken cancellationToken = default)
    {
        var path = GetSavePath(slot);
        var gate = GateFor(path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                var fromBackup = await TryReadBackupAsync(path, cancellationToken).ConfigureAwait(false);
                return fromBackup ?? new PolisSaveReadResult(PolisSaveDocument.Empty, PolisSaveDocument.CurrentSchemaVersion, 0, false, null);
            }

            var result = PolisSaveFormat.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
            if (result.FutureVersion is { } future)
            {
                // 新版应用写的存档：这一版读不懂它，也绝不能拿一份空档盖掉它。另存一份，从空档开始。
                var keep = path + $".v{future}.bak";
                File.Move(path, keep, overwrite: true);
                _logger.Warning(
                    "Polis save {Path} has schema version {Future}, newer than {Current}; kept it as {Kept} and started a fresh save",
                    path, future, PolisSaveDocument.CurrentSchemaVersion, keep);
                return result;
            }

            if (result.Unreadable)
            {
                var corrupt = path + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
                File.Move(path, corrupt, overwrite: true);
                var fromBackup = await TryReadBackupAsync(path, cancellationToken).ConfigureAwait(false);
                _logger.Warning(
                    "Polis save {Path} is unreadable; kept it as {Corrupt} and {Fallback}",
                    path, corrupt, fromBackup == null ? "started a fresh save" : "restored the previous good copy");
                return fromBackup ?? result;
            }

            if (result.QuarantinedRecords > 0)
            {
                _logger.Warning(
                    "Polis save {Path}: isolated {Count} unreadable record(s); the rest of the city loaded and the originals stay in the save",
                    path, result.QuarantinedRecords);
            }
            if (result.MigratedFromVersion != PolisSaveDocument.CurrentSchemaVersion)
            {
                _logger.Information("Polis save {Path} migrated from schema {From} to {To}",
                    path, result.MigratedFromVersion, PolisSaveDocument.CurrentSchemaVersion);
            }
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<PolisSaveReadResult?> TryReadBackupAsync(string path, CancellationToken cancellationToken)
    {
        var backup = path + ".bak";
        if (!File.Exists(backup)) return null;
        var result = PolisSaveFormat.Parse(await File.ReadAllTextAsync(backup, cancellationToken).ConfigureAwait(false));
        return result.Unreadable || result.FutureVersion != null ? null : result;
    }

    public async Task SaveAsync(PolisSaveSlot slot, PolisSaveDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var path = GetSavePath(slot);
        var gate = GateFor(path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // 上一份完好的副本：只有当前文件读得懂才轮换成 .bak，坏文件绝不顶掉好的备份
            if (File.Exists(path))
            {
                var current = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                var parsed = PolisSaveFormat.Parse(current);
                if (!parsed.Unreadable && parsed.FutureVersion == null)
                    File.Copy(path, path + ".bak", overwrite: true);
            }
            await WriteAtomicAsync(path, PolisSaveFormat.Serialize(document), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PolisIndexDocument?> LoadIndexAsync(PolisSaveSlot slot, CancellationToken cancellationToken = default)
    {
        var path = GetIndexPath(slot);
        if (!File.Exists(path)) return null;
        try
        {
            var index = PolisSaveFormat.ParseIndex(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
            if (index == null)
                _logger.Information("Polis index {Path} is unreadable or from another version; it will be rebuilt by the next scan", path);
            return index;
        }
        catch (IOException ex)
        {
            _logger.Information(ex, "Polis index {Path} could not be read; it will be rebuilt by the next scan", path);
            return null;
        }
    }

    public async Task SaveIndexAsync(PolisSaveSlot slot, PolisIndexDocument index, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        var path = GetIndexPath(slot);
        var gate = GateFor(path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await WriteAtomicAsync(path, PolisSaveFormat.SerializeIndex(index), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private SemaphoreSlim GateFor(string path) => _gates.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));

    /// <summary>先写同目录下的临时文件并刷到盘，再整份替换：断电只会留下旧文件或新文件，不会留下半个。</summary>
    private static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (IOException)
            {
                // 临时文件删不掉不影响结果：替换要么已经完成，要么原异常正在往上抛。
            }
        }
    }
}
