using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.GameMode;

/// <summary>
/// 关系的账本（第三轮需求 1）：全局的 <c>AthenaData/Game/relations.json</c>——只有事件带来的变化（起点按真实的数现推，不存），
/// 不属于任何一座城的存档（"居民从不存盘"照旧：存的是发生过什么，不是居民）。写法照 <see cref="PolisContestStore"/>：
/// 先写同目录的临时文件再整份替换；读不懂的文件（坏了、或者是更新的版本写的）原样改名留着（<c>.corrupt-*</c>），从空的账本重新记。
/// 只有 Id、数和时刻，没有任何消息正文。
/// </summary>
public sealed class PolisRelationStore : IDisposable
{
    public const int SchemaVersion = 1;
    /// <summary>最多记多少个人、多少对（最旧的先丢）。</summary>
    public const int MaxPeople = 2000;
    public const int MaxPairs = 5000;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PolisRelationStore(string path, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<PolisRelationStore>();
    }

    public string FilePath => _path;

    public void Dispose() => _gate.Dispose();

    public async Task<PolisRelationLedger> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>整份写下（调用的人先 <see cref="PolisRelations.Prune"/>）。</summary>
    public async Task SaveAsync(PolisRelationLedger ledger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var you = ledger.You.OrderByDescending(t => t.At).Take(MaxPeople).Select(PersonEntry.From).ToList();
            var pairs = ledger.Pairs.OrderByDescending(p => p.At).Take(MaxPairs).Select(PairEntry.From).ToList();
            var document = new RelationFile { SchemaVersion = SchemaVersion, You = you, Pairs = pairs };
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await WriteAtomicAsync(_path, JsonSerializer.Serialize(document, Json), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PolisRelationLedger> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return PolisRelationLedger.Empty;
        string text;
        try
        {
            text = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.Warning(ex, "Could not read the relations ledger {Path}; starting from the bases for now", _path);
            return PolisRelationLedger.Empty;
        }
        RelationFile? document;
        try
        {
            document = JsonSerializer.Deserialize<RelationFile>(text, Json);
        }
        catch (JsonException)
        {
            document = null;
        }
        if (document == null || document.SchemaVersion != SchemaVersion || document.You == null || document.Pairs == null)
        {
            // 坏了，或者是更新的版本写的：改名留着（不覆盖、不删），从空的账本重新记
            var keep = $"{_path}.corrupt-{DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";
            try
            {
                File.Move(_path, keep, overwrite: true);
                _logger.Warning("The relations ledger {Path} could not be read (schema {Schema}); kept it as {Keep} and started a new one",
                    _path, document?.SchemaVersion, keep);
            }
            catch (IOException ex)
            {
                _logger.Warning(ex, "The relations ledger {Path} could not be read and could not be set aside", _path);
            }
            return PolisRelationLedger.Empty;
        }
        var people = document.You.Where(p => !string.IsNullOrEmpty(p.Id) && double.IsFinite(p.Delta)).Select(p => p.ToTie()).ToList();
        var pairs = document.Pairs
            .Where(p => !string.IsNullOrEmpty(p.A) && !string.IsNullOrEmpty(p.B) && double.IsFinite(p.Ab) && double.IsFinite(p.Ba))
            .Select(p => p.ToTie()).ToList();
        return new PolisRelationLedger(people, pairs);
    }

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

    private sealed class RelationFile
    {
        public int SchemaVersion { get; set; }
        public List<PersonEntry>? You { get; set; }
        public List<PairEntry>? Pairs { get; set; }
    }

    private sealed class MarkEntry
    {
        public PolisRelationEvent Event { get; set; }
        public DateTimeOffset At { get; set; }
        public double Delta { get; set; }
    }

    private sealed class PersonEntry
    {
        public string? Id { get; set; }
        public double Delta { get; set; }
        public DateTimeOffset At { get; set; }
        public List<MarkEntry>? Recent { get; set; }

        public static PersonEntry From(PolisPlayerTie t) => new()
        {
            Id = t.Id,
            Delta = Math.Round(t.Delta, 3),
            At = t.At,
            Recent = t.Recent.Count == 0 ? null : t.Recent.Select(m => new MarkEntry { Event = m.Event, At = m.At, Delta = Math.Round(m.Delta, 3) }).ToList(),
        };

        public PolisPlayerTie ToTie() => new(Id!, Math.Clamp(Delta, -PolisRelations.MaxPlayerDelta, PolisRelations.MaxPlayerDelta), At,
            (Recent ?? new List<MarkEntry>()).Where(m => Enum.IsDefined(m.Event) && double.IsFinite(m.Delta)).Select(m => new PolisRelationMark(m.Event, m.At, m.Delta)).ToList());
    }

    private sealed class PairEntry
    {
        public string? A { get; set; }
        public string? B { get; set; }
        public double Ab { get; set; }
        public double Ba { get; set; }
        public DateTimeOffset At { get; set; }

        public static PairEntry From(PolisPairTie p) => new() { A = p.A, B = p.B, Ab = Math.Round(p.Ab, 3), Ba = Math.Round(p.Ba, 3), At = p.At };

        public PolisPairTie ToTie()
        {
            // 两头按序数排（账本的约定）；读到反过来的就换回来
            var (a, b, ab, ba) = string.CompareOrdinal(A!, B!) <= 0 ? (A!, B!, Ab, Ba) : (B!, A!, Ba, Ab);
            return new PolisPairTie(a, b, Math.Clamp(ab, 0, PolisRelations.MaxAmbientPairDelta), Math.Clamp(ba, 0, PolisRelations.MaxAmbientPairDelta), At);
        }
    }
}
