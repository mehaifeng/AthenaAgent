using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Athena.UI.Services.GameMode;
using Microsoft.Data.Sqlite;

// 用法（仓库根目录；先 dotnet build Tools/GameMode/PolisExport）：
//   dotnet Tools/GameMode/PolisExport/bin/Debug/net10.0/PolisExport.dll synthetic --out Tools/GameMode/web/fixtures/synthetic.json
//   dotnet …/PolisExport.dll list --db <AthenaData/conversations.db> [--workspace-id <id>]
//   dotnet …/PolisExport.dll fixture --root <工作区目录> --out Tools/GameMode/.local/real-fixture.json [--ledger <账本文件>] [--max-entries N]
// 由真实数据导出的夹具含真实的文件夹名和路径，只写进被 .gitignore 忽略的 Tools/GameMode/.local/。
// 2026-10-13 起夹具只有城邦快照：回放（把归档会话转成事件）连同直播一起拿掉了，list 只数会话的回合与工具调用。

var command = args.Length > 0 ? args[0] : "help";
var options = ParseOptions(args.Skip(1).ToArray());

try
{
    switch (command)
    {
        case "synthetic":
            Write(PolisFixture.ToJson(PolisSyntheticFixture.Build()), Opt("out"));
            return 0;

        case "list":
            ListConversations(Required("db"), Opt("workspace-id"));
            return 0;

        case "fixture":
            ExportFixture();
            return 0;

        default:
            Console.Error.WriteLine("命令：synthetic | list | fixture（参数见 Program.cs 文件头）");
            return command == "help" ? 0 : 2;
    }
}
catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or SqliteException or JsonException)
{
    Console.Error.WriteLine($"PolisExport: {ex.Message}");
    return 1;
}

void ExportFixture()
{
    var root = Path.GetFullPath(Required("root"));
    if (!Directory.Exists(root)) throw new ArgumentException($"工作区目录不存在：{root}");
    var now = DateTimeOffset.UtcNow;

    var scanOptions = Opt("max-entries") is { } max ? new PolisScanOptions { MaxEntries = int.Parse(max, System.Globalization.CultureInfo.InvariantCulture) } : PolisScanOptions.Default;
    var scan = PolisScanner.Scan(PolisFileSystem.Enumerator(root), scanOptions);
    var summary = PolisBuildings.Summarize(scan, now);

    var ledgerPath = Opt("ledger");
    var previous = ledgerPath != null && File.Exists(ledgerPath) ? PolisFixture.LedgerFromJson(File.ReadAllText(ledgerPath)) : PolisLedger.Empty;
    var layout = PolisLayoutLedger.Update(previous, summary.Buildings);
    if (ledgerPath != null) File.WriteAllText(ledgerPath, PolisFixture.LedgerToJson(layout.Ledger));

    var fixture = PolisFixture.Compose(new DirectoryInfo(root).Name, scan, summary, layout, now, synthetic: false);
    Write(PolisFixture.ToJson(fixture), Required("out"));

    Console.Error.WriteLine(
        $"城邦：{fixture.Buildings.Count} 座建筑（{string.Join("、", fixture.Buildings.GroupBy(b => b.Kind).Select(g => $"{g.Key} {g.Count()}"))}），" +
        $"市集摊位 {fixture.Market.Count}，扫描 {scan.VisitedEntries} 项{(scan.Truncated ? "（未完全测绘）" : string.Empty)}");
}

// 归档负载 → 只数角色：回合数（可见的用户消息）、工具调用（tool 消息）、有总时长的气泡。正文在这里读完就丢。
static (int Turns, int ToolCalls, int Timed, string First) CountMessages(string payload)
{
    using var doc = JsonDocument.Parse(payload);
    int turns = 0, toolCalls = 0, timed = 0;
    string first = "-";
    if (!doc.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) return (0, 0, 0, first);
    foreach (var m in messages.EnumerateArray())
    {
        var role = Str(m, "role") ?? string.Empty;
        var hidden = m.TryGetProperty("isHidden", out var h) && h.ValueKind == JsonValueKind.True;
        if (first == "-" && DateTimeOffset.TryParse(Str(m, "timestamp"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var timestamp))
            first = timestamp.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (role == "user" && !hidden) turns++;
        else if (role == "tool") toolCalls++;
        else if (role == "assistant" && !hidden && m.TryGetProperty("durationMs", out var d) && d.ValueKind == JsonValueKind.Number && d.GetInt64() > 0) timed++;
    }
    return (turns, toolCalls, timed, first);
}

static void ListConversations(string dbPath, string? workspaceId)
{
    using var connection = Open(dbPath);
    using var query = connection.CreateCommand();
    query.CommandText = "select conversation_id, workspace_id, payload from conversations order by updated_at desc";
    using var reader = query.ExecuteReader();
    Console.WriteLine("会话前缀  回合  工具调用  有总时长的气泡  首条消息日期");
    while (reader.Read())
    {
        if (workspaceId != null && !string.Equals(reader.IsDBNull(1) ? null : reader.GetString(1), workspaceId, StringComparison.OrdinalIgnoreCase)) continue;
        var (turns, toolCalls, timed, first) = CountMessages(reader.GetString(2));
        Console.WriteLine($"{reader.GetString(0)[..8]}  {turns,4}  {toolCalls,8}  {timed,14}  {first}");
    }
}

static SqliteConnection Open(string dbPath)
{
    if (!File.Exists(dbPath)) throw new ArgumentException($"数据库不存在：{dbPath}");
    // 只读 + immutable：不建 -shm/-wal，不碰正在用的库（应用开着时会漏掉还没检查点的最后几分钟，导出一段旧会话不受影响）
    var connection = new SqliteConnection($"Data Source=file:{Path.GetFullPath(dbPath)}?immutable=1;Mode=ReadOnly");
    connection.Open();
    return connection;
}

static string? Str(JsonElement element, string name)
    => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

static void Write(string text, string? path)
{
    if (path is null)
    {
        Console.Out.Write(text);
        return;
    }
    var full = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    File.WriteAllText(full, text);
    Console.Error.WriteLine($"已写入 {full}");
}

static Dictionary<string, string> ParseOptions(string[] rest)
{
    var map = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < rest.Length; i++)
    {
        if (!rest[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"无法识别的参数：{rest[i]}");
        if (i + 1 >= rest.Length) throw new ArgumentException($"参数 {rest[i]} 缺少值");
        map[rest[i][2..]] = rest[++i];
    }
    return map;
}

string? Opt(string name) => options.TryGetValue(name, out var value) ? value : null;

string Required(string name) => Opt(name) ?? throw new ArgumentException($"缺少参数 --{name}");
