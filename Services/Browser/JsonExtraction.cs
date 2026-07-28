using System.Text.Json;

namespace Athena.UI.Services.Browser;

/// <summary>
/// 从可能夹带围栏/前后散文的模型输出里，安全提取"第一个配平的顶层 JSON 对象"。
/// 关键：读到根对象配平即停，忽略其后的一切内容——即便模型在 JSON 之后追加了含花括号的
/// 解释文字（例如引用 MCP 配置片段、<c>${TOKEN}</c> 模板），也不会像
/// <c>IndexOf('{')…LastIndexOf('}')</c> 那样把尾巴一并吞进去导致
/// "'T' is invalid after a single JSON value" 之类的解析崩溃。
/// </summary>
internal static class JsonExtraction
{
    /// <summary>提取第一个配平的顶层 JSON 对象；找不到则抛 <see cref="JsonException"/>。</summary>
    public static string ExtractFirstJsonObject(string content)
    {
        if (TryExtractFirstJsonObject(content, out var json))
        {
            return json;
        }

        throw new JsonException("No balanced JSON object found in model output.");
    }

    /// <summary>尝试提取第一个配平的顶层 JSON 对象。</summary>
    public static bool TryExtractFirstJsonObject(string? content, out string json)
    {
        json = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var start = content.IndexOf('{');
        if (start < 0)
        {
            return false;
        }

        // 手写扫描：跟踪字符串状态与转义，按花括号配平定位根对象收尾。
        // 不直接 Utf8JsonReader 一把梭，是因为要在"根值结束"处主动停下并丢弃其后内容。
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < content.Length; i++)
        {
            var c = content[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        var candidate = content.Substring(start, i - start + 1);
                        // 再做一次严格解析确认配平结果确为合法 JSON；不合法则视为未找到。
                        try
                        {
                            using var _ = JsonDocument.Parse(candidate);
                        }
                        catch (JsonException)
                        {
                            return false;
                        }

                        json = candidate;
                        return true;
                    }

                    break;
            }
        }

        return false;
    }
}
