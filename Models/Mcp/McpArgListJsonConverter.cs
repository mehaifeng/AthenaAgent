using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Athena.UI.Models.Mcp;

/// <summary>
/// 把 <c>ObservableCollection&lt;McpArgEntry&gt;</c> 序列化为扁平字符串数组（["-y","pkg"]），
/// 从而在磁盘与 Claude Desktop 导入格式之间保持一致；内存里用包装对象以支持 TextBox 双向编辑。
/// </summary>
public sealed class McpArgListJsonConverter : JsonConverter<ObservableCollection<McpArgEntry>>
{
    public override ObservableCollection<McpArgEntry> Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new ObservableCollection<McpArgEntry>();
        if (reader.TokenType == JsonTokenType.Null) return result;
        if (reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); return result; }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String)
                result.Add(new McpArgEntry { Value = reader.GetString() ?? string.Empty });
            else
                reader.Skip();
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, ObservableCollection<McpArgEntry> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var entry in value)
            writer.WriteStringValue(entry.Value);
        writer.WriteEndArray();
    }
}
