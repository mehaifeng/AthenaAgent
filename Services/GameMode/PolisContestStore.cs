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
/// 辩论的战绩（M-E）：全局的 <c>AthenaData/Game/contests.json</c>——你和雅典娜辩过的每一场，不属于任何一座城的存档
/// （换工作区、删工作区都不动它）。写法照 <see cref="PolisSaveStore"/>：先写同目录的临时文件再整份替换；
/// 读不懂的文件（坏了、或者是更新的版本写的）原样改名留着（<c>.corrupt-*</c>），从空的战绩重新记，不覆盖它。
/// </summary>
public sealed class PolisContestStore : IDisposable
{
    public const int SchemaVersion = 1;
    /// <summary>最多记多少场（最旧的先丢）。</summary>
    public const int MaxRecords = 500;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PolisContestStore(string path, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<PolisContestStore>();
    }

    public string FilePath => _path;

    public void Dispose() => _gate.Dispose();

    public async Task<IReadOnlyList<PolisContestRecord>> LoadAsync(CancellationToken cancellationToken = default)
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

    /// <summary>记一场，返回记过之后的全部战绩。</summary>
    public async Task<IReadOnlyList<PolisContestRecord>> AppendAsync(PolisContestRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = (await ReadAsync(cancellationToken).ConfigureAwait(false)).Append(record).ToList();
            if (records.Count > MaxRecords) records = records.Skip(records.Count - MaxRecords).ToList();
            var document = new ContestFile { SchemaVersion = SchemaVersion, Debates = records.Select(ContestEntry.From).ToList() };
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await WriteAtomicAsync(_path, JsonSerializer.Serialize(document, Json), cancellationToken).ConfigureAwait(false);
            return records;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<PolisContestRecord>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return Array.Empty<PolisContestRecord>();
        string text;
        try
        {
            text = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.Warning(ex, "Could not read the debate record {Path}; counting from empty for now", _path);
            return Array.Empty<PolisContestRecord>();
        }
        ContestFile? document;
        try
        {
            document = JsonSerializer.Deserialize<ContestFile>(text, Json);
        }
        catch (JsonException)
        {
            document = null;
        }
        if (document == null || document.SchemaVersion != SchemaVersion || document.Debates == null)
        {
            // 坏了，或者是更新的版本写的：改名留着（不覆盖、不删），从空的战绩重新记
            var keep = $"{_path}.corrupt-{DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";
            try
            {
                File.Move(_path, keep, overwrite: true);
                _logger.Warning("The debate record {Path} could not be read (schema {Schema}); kept it as {Keep} and started a new record",
                    _path, document?.SchemaVersion, keep);
            }
            catch (IOException ex)
            {
                _logger.Warning(ex, "The debate record {Path} could not be read and could not be set aside", _path);
            }
            return Array.Empty<PolisContestRecord>();
        }
        return document.Debates.Where(d => d.Motion != null && d.Winner is "you" or "athena" or "tie").Select(d => d.ToRecord()).ToList();
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

    private sealed class ContestFile
    {
        public int SchemaVersion { get; set; }
        public List<ContestEntry>? Debates { get; set; }
    }

    private sealed class ContestEntry
    {
        public DateTimeOffset At { get; set; }
        public string? Motion { get; set; }
        public int VotesForYou { get; set; }
        public string? Winner { get; set; }
        public bool Judged { get; set; }

        public static ContestEntry From(PolisContestRecord r) => new() { At = r.At, Motion = r.Motion, VotesForYou = r.VotesForYou, Winner = r.Winner, Judged = r.Judged };

        public PolisContestRecord ToRecord() => new(At, Motion!, Math.Clamp(VotesForYou, 0, PolisDebate.Jury), Winner!, Judged);
    }
}
