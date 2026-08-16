using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Athena.UI.Services.Functions;

/// <summary>
/// 工具结果的统一体量闸门。在此之前只有 <c>execute_terminal_command</c> 自己做了截断，
/// 其余工具（parse_office_document 的整份 Markdown、list_system_directory 的千条目录项、
/// search_in_file 的上万行上下文……）都是把 <see cref="FunctionResult.ToJson"/> 的全量结果
/// 直接灌进上下文，一次调用就可能吃掉整个上下文窗口。
///
/// 截断遵循三条原则：
/// 1. 先砍数组尾部——同构条目的尾部信息密度最低，砍掉的代价最小；
/// 2. 再对字符串叶子做「水位填充」——统一上限 C 使总量入预算，长文本被削、短元数据完整保留，
///    这样 path / totalChunks / nextStartRow 这类续读线索绝不会因为正文太长而丢失；
/// 3. 所有省略都显式标注（<c>truncationNote</c>），让模型知道自己拿到的是不完整结果、
///    应当缩小范围重取，而不是把截断当成事实全貌。
/// </summary>
public static class ToolResultTruncator
{
    /// <summary>单个字符串叶子无论如何都保留的字符数——低于此值内容已无参考价值。</summary>
    private const int MinStringCap = 400;

    /// <summary>数组无论如何都保留的元素数——让模型至少看得到条目形状。</summary>
    private const int MinArrayItems = 3;

    /// <summary>给 truncationNote 自身预留的字符预算。</summary>
    private const int NoteReserve = 300;

    /// <summary>
    /// <see cref="TerminalOutputTruncator.Process"/> 的 maxChars 是「内容」预算：它会在首尾之间
    /// 再插一条省略标注，因此输出比 maxChars 长。这里预留标注的长度，最后再做一次硬钳制，
    /// 使 <see cref="Apply"/> 的预算成为真正的硬上限。
    /// </summary>
    private const int MarkerAllowance = 160;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public readonly record struct Outcome(string Json, int OmittedChars, bool Truncated);

    /// <summary>
    /// 把一份序列化后的工具结果压进 <paramref name="budgetChars"/> 预算。未超预算时原样返回，
    /// 不做任何解析/重排，保证绝大多数调用零开销。
    /// </summary>
    public static Outcome Apply(string resultJson, int budgetChars)
    {
        if (budgetChars <= 0 || string.IsNullOrEmpty(resultJson) || resultJson.Length <= budgetChars)
            return new Outcome(resultJson, 0, false);

        int originalLength = resultJson.Length;

        JsonNode? root = null;
        try
        {
            root = JsonNode.Parse(resultJson);
        }
        catch (JsonException)
        {
            // 结果不是合法 JSON（理论上不会发生，工具结果由我们自己序列化）：退回纯文本首尾保留式截断。
            root = null;
        }

        if (root is null)
        {
            var flat = FlattenToBudget(resultJson, budgetChars);
            return new Outcome(flat, originalLength - flat.Length, true);
        }

        // —— 阶段 1：砍长数组的尾部 ——
        ShrinkArrays(root, budgetChars);

        // —— 阶段 2：字符串叶子水位填充 ——
        var afterArrays = root.ToJsonString(SerializerOptions);
        if (afterArrays.Length > budgetChars)
        {
            var lengths = new List<int>();
            CollectStringLengths(root, lengths);
            if (lengths.Count > 0)
            {
                // JSON 结构本身（键名、标点、数字）占掉的部分不可压缩，字符串预算是剩下的。
                long stringChars = lengths.Sum(static value => (long)value);
                long overhead = Math.Max(0, afterArrays.Length - stringChars);
                long allowance = budgetChars - overhead - NoteReserve;
                int cap = allowance <= 0
                    ? MinStringCap
                    : Math.Max(MinStringCap, WaterFillCap(lengths, allowance));
                TruncateStrings(root, cap);
            }
        }

        if (root is JsonObject rootObject)
        {
            rootObject["truncationNote"] =
                $"结果过大已压缩（原始 {originalLength} 字符，上限 {budgetChars}）。"
                + "被省略的部分都带有显式标记；如需完整内容，请缩小范围重新调用"
                + "（分页参数、更精确的过滤条件，或先把内容导出到文件再分段读取）。";
        }

        var json = root.ToJsonString(SerializerOptions);

        // —— 阶段 3：兜底 ——
        // 病理输入（成千上万个小键、深层嵌套结构）下前两阶段可能仍压不进预算。
        // 工具结果在协议上只是一个字符串，这里做首尾保留式硬截断，保证预算是硬上限。
        if (json.Length > budgetChars)
        {
            var flat = FlattenToBudget(json, budgetChars);
            return new Outcome(flat, originalLength - flat.Length, true);
        }

        return new Outcome(json, Math.Max(0, originalLength - json.Length), true);
    }

    /// <summary>
    /// 首尾保留式硬截断，保证输出严格不超过预算（含省略标注本身）。
    /// 尾部钳制时不把 UTF-16 代理对切成半个。
    /// </summary>
    private static string FlattenToBudget(string text, int budgetChars)
    {
        var processed = TerminalOutputTruncator.Process(text, Math.Max(1, budgetChars - MarkerAllowance)).Text;
        if (processed.Length <= budgetChars) return processed;

        var clamped = processed[..budgetChars];
        return clamped.Length > 0 && char.IsHighSurrogate(clamped[^1]) ? clamped[..^1] : clamped;
    }

    /// <summary>
    /// 把超长数组截成前缀 + 一条说明元素。单个数组的上限取预算的一半：既能让一个大数组
    /// 占据结果主体，又保证两个大数组不会各自撑满预算。
    /// </summary>
    private static void ShrinkArrays(JsonNode? node, int budgetChars)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(static pair => pair.Key).ToList())
                    ShrinkArrays(obj[key], budgetChars);
                break;

            case JsonArray array:
                foreach (var child in array.ToList())
                    ShrinkArrays(child, budgetChars);

                int count = array.Count;
                if (count <= MinArrayItems) return;

                int arrayLength = array.ToJsonString(SerializerOptions).Length;
                int arrayBudget = budgetChars / 2;
                if (arrayLength <= arrayBudget) return;

                int perItem = Math.Max(1, arrayLength / count);
                int keep = Math.Clamp(arrayBudget / perItem, MinArrayItems, count - 1);
                for (int i = count - 1; i >= keep; i--)
                    array.RemoveAt(i);
                array.Add($"[已省略 {count - keep} 项，共 {count} 项——请用更精确的过滤条件或分页参数重取]");
                break;
        }
    }

    private static void CollectStringLengths(JsonNode? node, List<int> lengths)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj)
                    CollectStringLengths(pair.Value, lengths);
                break;
            case JsonArray array:
                foreach (var child in array)
                    CollectStringLengths(child, lengths);
                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                lengths.Add(text.Length);
                break;
        }
    }

    /// <summary>
    /// 水位填充：求最大的统一上限 C，使 Σ min(len_i, C) ≤ allowance。
    /// 短字符串（路径、状态、续读游标）完整保留，只有长正文被削到 C。
    /// </summary>
    private static int WaterFillCap(List<int> lengths, long allowance)
    {
        var sorted = lengths.OrderBy(static value => value).ToList();
        long remaining = allowance;
        for (int i = 0; i < sorted.Count; i++)
        {
            long cap = remaining / (sorted.Count - i);
            if (sorted[i] > cap)
                return (int)Math.Min(int.MaxValue, cap);
            remaining -= sorted[i];
        }
        return int.MaxValue;
    }

    private static void TruncateStrings(JsonNode? node, int cap)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(static pair => pair.Key).ToList())
                {
                    if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        if (text.Length > cap)
                            obj[key] = TerminalOutputTruncator.Process(text, cap).Text;
                    }
                    else
                    {
                        TruncateStrings(obj[key], cap);
                    }
                }
                break;

            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        if (text.Length > cap)
                            array[i] = TerminalOutputTruncator.Process(text, cap).Text;
                    }
                    else
                    {
                        TruncateStrings(array[i], cap);
                    }
                }
                break;
        }
    }
}
