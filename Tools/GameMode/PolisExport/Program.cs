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
//   dotnet …/PolisExport.dll fixture --root <工作区目录> --db <conversations.db> --conversation <会话 id 前缀>
//                                     --out Tools/GameMode/.local/real-fixture.json [--ledger <账本文件>] [--home <主目录>]
// 由真实数据导出的夹具含真实的文件夹名和路径，只写进被 .gitignore 忽略的 Tools/GameMode/.local/。
// 回放事件里没有消息正文：工具结果只取 success 布尔值，推理只取长度，终端命令和查询词一概不读。

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
    var home = Opt("home") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var now = DateTimeOffset.UtcNow;

    var scanOptions = Opt("max-entries") is { } max ? new PolisScanOptions { MaxEntries = int.Parse(max, System.Globalization.CultureInfo.InvariantCulture) } : PolisScanOptions.Default;
    var scan = PolisScanner.Scan(PolisFileSystem.Enumerator(root), scanOptions);
    var summary = PolisBuildings.Summarize(scan, now);

    var ledgerPath = Opt("ledger");
    var previous = ledgerPath != null && File.Exists(ledgerPath) ? PolisFixture.LedgerFromJson(File.ReadAllText(ledgerPath)) : PolisLedger.Empty;
    var layout = PolisLayoutLedger.Update(previous, summary.Buildings);
    if (ledgerPath != null) File.WriteAllText(ledgerPath, PolisFixture.LedgerToJson(layout.Ledger));

    var messages = LoadConversation(Required("db"), Required("conversation"));
    var replay = PolisReplayConverter.Convert(messages, root, home);
    var fixture = PolisFixture.Compose(new DirectoryInfo(root).Name, scan, summary, layout, replay, now, synthetic: false);
    Write(PolisFixture.ToJson(fixture), Required("out"));

    Console.Error.WriteLine(
        $"城邦：{fixture.Buildings.Count} 座建筑（{string.Join("、", fixture.Buildings.GroupBy(b => b.Kind).Select(g => $"{g.Key} {g.Count()}"))}），" +
        $"市集摊位 {fixture.Market.Count}，扫描 {scan.VisitedEntries} 项{(scan.Truncated ? "（未完全测绘）" : string.Empty)}；" +
        $"回放：{replay.Turns} 回合、{replay.ToolCalls} 次工具调用、{replay.DurationMs / 1000.0:F1} 秒，剪掉空闲 {replay.CompressedGaps} 处");
}

static List<PolisReplayMessage> LoadConversation(string dbPath, string conversationPrefix)
{
    using var connection = Open(dbPath);
    using var query = connection.CreateCommand();
    query.CommandText = "select conversation_id, payload from conversations order by updated_at desc";
    using var reader = query.ExecuteReader();
    while (reader.Read())
    {
        if (!reader.GetString(0).StartsWith(conversationPrefix, StringComparison.OrdinalIgnoreCase)) continue;
        return ProjectMessages(reader.GetString(1));
    }
    throw new ArgumentException($"找不到 id 以 {conversationPrefix} 开头的会话");
}

// 归档负载 → 回放输入。正文在这里读完就丢：只留角色、时间、调用结构、成功与否、推理长度。
static List<PolisReplayMessage> ProjectMessages(string payload)
{
    using var doc = JsonDocument.Parse(payload);
    var result = new List<PolisReplayMessage>();
    if (!doc.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) return result;
    foreach (var m in messages.EnumerateArray())
    {
        var role = Str(m, "role") ?? string.Empty;
        if (!DateTimeOffset.TryParse(Str(m, "timestamp"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var timestamp)) continue;
        bool? succeeded = null;
        if (role == "tool" && Str(m, "content") is { } content)
        {
            try
            {
                using var parsed = JsonDocument.Parse(content);
                if (parsed.RootElement.ValueKind == JsonValueKind.Object
                    && parsed.RootElement.TryGetProperty("success", out var success)
                    && success.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    succeeded = success.GetBoolean();
                }
            }
            catch (JsonException)
            {
                // 结果不是 JSON（老消息）：成功与否未知，回放按失败处理
            }
        }

        result.Add(new PolisReplayMessage(
            role,
            timestamp,
            m.TryGetProperty("isHidden", out var hidden) && hidden.ValueKind == JsonValueKind.True,
            Str(m, "toolCallsJson"),
            Str(m, "toolCallId"),
            succeeded,
            m.TryGetProperty("durationMs", out var duration) && duration.ValueKind == JsonValueKind.Number ? duration.GetInt64() : null,
            Str(m, "reasoningContent")?.Length ?? 0));
    }
    return result;
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
        var messages = ProjectMessages(reader.GetString(2));
        var replay = PolisReplayConverter.Convert(messages, "/", null);
        var timed = messages.Count(m => m.Role == "assistant" && !m.IsHidden && m.DurationMs > 0);
        var first = messages.Count == 0 ? "-" : messages[0].Timestamp.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Console.WriteLine($"{reader.GetString(0)[..8]}  {replay.Turns,4}  {replay.ToolCalls,8}  {timed,14}  {first}");
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
