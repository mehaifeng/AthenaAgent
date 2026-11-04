using System.Text.Json;

namespace Ptc.Core;

/// <summary>
/// Explicit JSON Schema subset. Unknown constraints and all references fail closed at catalog admission.
/// An empty schema accepts all JSON; absent additionalProperties permits extras, false closes the object.
/// </summary>
public static class PtcSchema
{
    private static readonly HashSet<string> Annotations = new(StringComparer.Ordinal)
    { "description", "title", "default", "examples", "$comment" };
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "type", "properties", "required", "additionalProperties", "items", "enum", "const",
        "minLength", "maxLength", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum",
        "minItems", "maxItems", "allOf", "anyOf", "oneOf", "not"
    };
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    { "object", "array", "string", "number", "integer", "boolean", "null" };

    public static void AssertSupported(JsonElement schema)
    {
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False) return;
        if (schema.ValueKind != JsonValueKind.Object) Unsupported();
        foreach (var property in schema.EnumerateObject())
        {
            var key = property.Name;
            var value = property.Value;
            if (Annotations.Contains(key)) continue;
            if (!Keywords.Contains(key)) Unsupported();
            switch (key)
            {
                case "type":
                    if (value.ValueKind == JsonValueKind.String)
                    { if (!Types.Contains(value.GetString()!)) Unsupported(); }
                    else if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0)
                    { foreach (var item in value.EnumerateArray()) if (item.ValueKind != JsonValueKind.String || !Types.Contains(item.GetString()!)) Unsupported(); }
                    else Unsupported();
                    break;
                case "properties":
                    if (value.ValueKind != JsonValueKind.Object) Unsupported();
                    foreach (var item in value.EnumerateObject()) AssertSupported(item.Value);
                    break;
                case "additionalProperties": case "items": case "not":
                    AssertSupported(value);
                    break;
                case "allOf": case "anyOf": case "oneOf":
                    if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0) Unsupported();
                    foreach (var item in value.EnumerateArray()) AssertSupported(item);
                    break;
                case "required":
                    if (value.ValueKind != JsonValueKind.Array) Unsupported();
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var item in value.EnumerateArray())
                        if (item.ValueKind != JsonValueKind.String || !names.Add(item.GetString()!)) Unsupported();
                    break;
                case "enum":
                    if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0) Unsupported();
                    break;
                case "const": break;
                case "minLength": case "maxLength": case "minItems": case "maxItems":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var count) || count < 0) Unsupported();
                    break;
                default:
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number)) Unsupported();
                    break;
            }
        }
    }

    public static bool IsValid(JsonElement schema, JsonElement value)
    {
        if (schema.ValueKind == JsonValueKind.True) return true;
        if (schema.ValueKind == JsonValueKind.False) return false;
        if (schema.TryGetProperty("type", out var type))
        {
            var match = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Any(item => MatchesType(item.GetString()!, value))
                : MatchesType(type.GetString()!, value);
            if (!match) return false;
        }
        if (schema.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(item => JsonElement.DeepEquals(item, value))) return false;
        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(constant, value)) return false;
        if (schema.TryGetProperty("allOf", out var all) && !all.EnumerateArray().All(item => IsValid(item, value))) return false;
        if (schema.TryGetProperty("anyOf", out var any) && !any.EnumerateArray().Any(item => IsValid(item, value))) return false;
        if (schema.TryGetProperty("oneOf", out var one) && one.EnumerateArray().Count(item => IsValid(item, value)) != 1) return false;
        if (schema.TryGetProperty("not", out var not) && IsValid(not, value)) return false;
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                if (schema.TryGetProperty("required", out var required)
                    && required.EnumerateArray().Any(item => !value.TryGetProperty(item.GetString()!, out _))) return false;
                var hasProperties = schema.TryGetProperty("properties", out var properties);
                foreach (var property in value.EnumerateObject())
                {
                    if (hasProperties && properties.TryGetProperty(property.Name, out var child))
                    { if (!IsValid(child, property.Value)) return false; }
                    else if (schema.TryGetProperty("additionalProperties", out var additional) && !IsValid(additional, property.Value)) return false;
                }
                break;
            case JsonValueKind.Array:
                if (!InRange(schema, value.GetArrayLength(), "minItems", "maxItems")) return false;
                if (schema.TryGetProperty("items", out var items) && value.EnumerateArray().Any(item => !IsValid(items, item))) return false;
                break;
            case JsonValueKind.String:
                if (!InRange(schema, value.GetString()!.EnumerateRunes().Count(), "minLength", "maxLength")) return false;
                break;
            case JsonValueKind.Number:
                var number = value.GetDouble();
                if (!InRange(schema, number, "minimum", "maximum")) return false;
                if (schema.TryGetProperty("exclusiveMinimum", out var min) && number <= min.GetDouble()) return false;
                if (schema.TryGetProperty("exclusiveMaximum", out var max) && number >= max.GetDouble()) return false;
                break;
        }
        return true;
    }

    private static bool InRange(JsonElement schema, double value, string minKey, string maxKey)
        => (!schema.TryGetProperty(minKey, out var min) || value >= min.GetDouble())
           && (!schema.TryGetProperty(maxKey, out var max) || value <= max.GetDouble());

    private static bool MatchesType(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && Math.Truncate(value.GetDouble()) == value.GetDouble(),
        _ => false
    };

    private static void Unsupported() => throw new PtcRuntimeException("schema_unsupported", "Tool schema uses an unsupported or malformed constraint.", PtcRunStatus.Rejected);
}
