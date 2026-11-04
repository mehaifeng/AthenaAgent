using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ptc.Core;

/// <summary>Strict UTF-8 JSON boundary; returned elements always own a cloned backing document.</summary>
public static class PtcJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static JsonElement Parse(string json, int maxBytes = 512 * 1024, int maxDepth = 32, bool requireObject = false)
    {
        if (CountText(json) > maxBytes)
            throw new PtcRuntimeException("data_limit_exceeded", "JSON exceeds its UTF-8 byte limit.", PtcRunStatus.BudgetExceeded);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = maxDepth, AllowDuplicateProperties = false
            });
            var value = document.RootElement;
            if (requireObject && value.ValueKind != JsonValueKind.Object)
                throw new PtcRuntimeException("invalid_arguments", "JSON must be an object.");
            ValidateValues(value);
            return value.Clone();
        }
        catch (JsonException)
        {
            throw new PtcRuntimeException("invalid_arguments", "Invalid, duplicate-property or over-depth JSON.");
        }
        catch (InvalidOperationException)
        {
            throw new PtcRuntimeException("invalid_arguments", "Invalid Unicode in JSON.");
        }
    }

    public static JsonElement Snapshot(JsonElement value, int maxBytes, int maxDepth, bool requireObject = false)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
            throw new PtcRuntimeException("invalid_arguments", "Undefined is not JSON.");
        return Parse(value.GetRawText(), maxBytes, maxDepth, requireObject);
    }

    public static int ByteCount(JsonElement value) => CountText(value.GetRawText());

    internal static int CountText(string text)
    {
        try { return StrictUtf8.GetByteCount(text); }
        catch (EncoderFallbackException)
        { throw new PtcRuntimeException("invalid_arguments", "Unpaired Unicode surrogate is not a valid JSON string."); }
    }

    private static void ValidateValues(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetDouble(out var number) || !double.IsFinite(number)
                || (Math.Truncate(number) == number && Math.Abs(number) > 9_007_199_254_740_991d))
                throw new PtcRuntimeException("invalid_arguments", "Numbers must be finite; integers outside JavaScript's safe range must use strings.");
        }
        else if (value.ValueKind == JsonValueKind.String)
            CountText(value.GetString()!);
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) { CountText(property.Name); ValidateValues(property.Value); }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateValues(item);
    }
}
