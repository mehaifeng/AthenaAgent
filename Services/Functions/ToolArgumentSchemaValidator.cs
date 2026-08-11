using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Athena.UI.Services.Functions;

/// <summary>
/// Enforces the JSON-Schema subset used by built-in tools before approval or execution.
/// This keeps schemas from being advisory-only on OpenAI-compatible endpoints that do not
/// support strict structured outputs.
/// </summary>
internal static class ToolArgumentSchemaValidator
{
    private const int MaxErrors = 8;

    public static JsonElement NormalizeAndClose(object schema)
    {
        var node = JsonSerializer.SerializeToNode(schema)
            ?? throw new InvalidOperationException("Tool schema serialization returned null.");
        CloseDeclaredObjects(node);
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    public static void AssertDelegateContract(string toolName, Delegate function, JsonElement schema)
    {
        if (!schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Tool '{toolName}' must declare a top-level object with properties.");

        var schemaNames = properties.EnumerateObject()
            .Select(property => Canonicalize(property.Name))
            .ToHashSet(StringComparer.Ordinal);
        var parameterNames = function.Method.GetParameters()
            .Select(parameter => Canonicalize(parameter.Name ?? string.Empty))
            .ToHashSet(StringComparer.Ordinal);

        if (!schemaNames.SetEquals(parameterNames))
        {
            var onlySchema = schemaNames.Except(parameterNames).OrderBy(value => value);
            var onlyMethod = parameterNames.Except(schemaNames).OrderBy(value => value);
            throw new InvalidOperationException(
                $"Tool '{toolName}' schema/delegate mismatch. Schema-only=[{string.Join(",", onlySchema)}], method-only=[{string.Join(",", onlyMethod)}].");
        }
    }

    public static bool TryValidate(JsonElement schema, JsonElement value, out string? error)
    {
        var errors = new List<string>();
        ValidateNode(schema, value, "$", errors);
        error = errors.Count == 0 ? null : string.Join("; ", errors.Take(MaxErrors));
        return errors.Count == 0;
    }

    private static void ValidateNode(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        if (errors.Count >= MaxErrors || schema.ValueKind != JsonValueKind.Object) return;

        if (schema.TryGetProperty("allOf", out var allOf) && allOf.ValueKind == JsonValueKind.Array)
        {
            foreach (var branch in allOf.EnumerateArray())
                ValidateNode(branch, value, path, errors);
        }

        if (schema.TryGetProperty("anyOf", out var anyOf) && anyOf.ValueKind == JsonValueKind.Array)
        {
            var matched = anyOf.EnumerateArray().Any(branch => BranchMatches(branch, value, path));
            if (!matched) AddError(errors, $"{path} must match at least one allowed shape.");
        }

        if (schema.TryGetProperty("oneOf", out var oneOf) && oneOf.ValueKind == JsonValueKind.Array)
        {
            var matches = oneOf.EnumerateArray().Count(branch => BranchMatches(branch, value, path));
            if (matches != 1) AddError(errors, $"{path} must match exactly one allowed shape (matched {matches}).");
        }

        if (schema.TryGetProperty("not", out var notSchema))
        {
            var notErrors = new List<string>();
            ValidateNode(notSchema, value, path, notErrors);
            if (notErrors.Count == 0) AddError(errors, $"{path} matches a forbidden shape.");
        }

        if (schema.TryGetProperty("type", out var typeElement) && !MatchesType(typeElement, value))
        {
            AddError(errors, $"{path} has type {value.ValueKind}, expected {TypeDescription(typeElement)}.");
            return;
        }

        if (schema.TryGetProperty("enum", out var enumElement) && enumElement.ValueKind == JsonValueKind.Array)
        {
            var matched = enumElement.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value));
            if (!matched) AddError(errors, $"{path} must be one of [{string.Join(", ", enumElement.EnumerateArray().Select(item => item.ToString()))}].");
        }

        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(constant, value))
            AddError(errors, $"{path} must equal {constant}.");

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                ValidateObject(schema, value, path, errors);
                break;
            case JsonValueKind.Array:
                ValidateArray(schema, value, path, errors);
                break;
            case JsonValueKind.String:
                ValidateString(schema, value.GetString() ?? string.Empty, path, errors);
                break;
            case JsonValueKind.Number:
                ValidateNumber(schema, value, path, errors);
                break;
        }
    }

    private static void ValidateObject(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        var propertyCount = value.EnumerateObject().Count();
        if (TryGetInt(schema, "minProperties", out var minProperties) && propertyCount < minProperties)
            AddError(errors, $"{path} must contain at least {minProperties} propert{(minProperties == 1 ? "y" : "ies")}.");
        if (TryGetInt(schema, "maxProperties", out var maxProperties) && propertyCount > maxProperties)
            AddError(errors, $"{path} must contain at most {maxProperties} properties.");

        var duplicateAliases = value.EnumerateObject()
            .GroupBy(property => Canonicalize(property.Name), StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateAliases != null)
            AddError(errors, $"{path} contains duplicate aliases for '{duplicateAliases.Key}'.");

        var knownProperties = new Dictionary<string, (string DeclaredName, JsonElement Schema)>(StringComparer.Ordinal);
        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
                knownProperties[Canonicalize(property.Name)] = (property.Name, property.Value);
        }

        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var requiredName in required.EnumerateArray().Select(item => item.GetString()).Where(name => name != null))
            {
                var requiredCanonical = Canonicalize(requiredName!);
                if (!value.EnumerateObject().Any(property => Canonicalize(property.Name) == requiredCanonical))
                    AddError(errors, $"{path}.{requiredName} is required.");
            }
        }

        foreach (var property in value.EnumerateObject())
        {
            if (knownProperties.TryGetValue(Canonicalize(property.Name), out var declared))
            {
                ValidateNode(declared.Schema, property.Value, $"{path}.{declared.DeclaredName}", errors);
                continue;
            }

            if (!schema.TryGetProperty("additionalProperties", out var additional)) continue;
            if (additional.ValueKind == JsonValueKind.False)
                AddError(errors, $"{path}.{property.Name} is not an allowed property.");
            else if (additional.ValueKind == JsonValueKind.Object)
                ValidateNode(additional, property.Value, $"{path}.{property.Name}", errors);
        }
    }

    private static void ValidateArray(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        var count = value.GetArrayLength();
        if (TryGetInt(schema, "minItems", out var minItems) && count < minItems)
            AddError(errors, $"{path} must contain at least {minItems} item(s).");
        if (TryGetInt(schema, "maxItems", out var maxItems) && count > maxItems)
            AddError(errors, $"{path} must contain at most {maxItems} item(s).");
        if (schema.TryGetProperty("uniqueItems", out var uniqueItems)
            && uniqueItems.ValueKind == JsonValueKind.True)
        {
            var items = value.EnumerateArray().ToArray();
            if (items.Where((item, index) => items.Take(index).Any(previous => JsonElement.DeepEquals(previous, item))).Any())
                AddError(errors, $"{path} must not contain duplicate items.");
        }
        if (schema.TryGetProperty("items", out var itemSchema))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                ValidateNode(itemSchema, item, $"{path}[{index++}]", errors);
        }
    }

    private static void ValidateString(JsonElement schema, string value, string path, List<string> errors)
    {
        if (TryGetInt(schema, "minLength", out var minLength) && value.Length < minLength)
            AddError(errors, $"{path} must contain at least {minLength} character(s).");
        if (TryGetInt(schema, "maxLength", out var maxLength) && value.Length > maxLength)
            AddError(errors, $"{path} must contain at most {maxLength} character(s).");
        if (schema.TryGetProperty("pattern", out var pattern) && pattern.ValueKind == JsonValueKind.String)
        {
            try
            {
                if (!Regex.IsMatch(value, pattern.GetString()!))
                    AddError(errors, $"{path} does not match the required pattern.");
            }
            catch (ArgumentException ex)
            {
                AddError(errors, $"{path} cannot be validated because the registered pattern is invalid: {ex.Message}");
            }
        }
    }

    private static void ValidateNumber(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        if (!value.TryGetDouble(out var number)) return;
        if (TryGetDouble(schema, "minimum", out var minimum) && number < minimum)
            AddError(errors, $"{path} must be >= {minimum.ToString(CultureInfo.InvariantCulture)}.");
        if (TryGetDouble(schema, "maximum", out var maximum) && number > maximum)
            AddError(errors, $"{path} must be <= {maximum.ToString(CultureInfo.InvariantCulture)}.");
    }

    private static bool MatchesType(JsonElement typeElement, JsonElement value)
    {
        if (typeElement.ValueKind == JsonValueKind.String)
            return MatchesSingleType(typeElement.GetString(), value);
        return typeElement.ValueKind == JsonValueKind.Array
               && typeElement.EnumerateArray().Any(item => MatchesSingleType(item.GetString(), value));
    }

    private static bool MatchesSingleType(string? type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true
    };

    private static string TypeDescription(JsonElement typeElement) => typeElement.ValueKind == JsonValueKind.Array
        ? string.Join("|", typeElement.EnumerateArray().Select(item => item.GetString()))
        : typeElement.GetString() ?? "unknown";

    private static bool TryGetInt(JsonElement schema, string name, out int value)
    {
        value = 0;
        return schema.TryGetProperty(name, out var element) && element.TryGetInt32(out value);
    }

    private static bool TryGetDouble(JsonElement schema, string name, out double value)
    {
        value = 0;
        return schema.TryGetProperty(name, out var element) && element.TryGetDouble(out value);
    }

    private static void CloseDeclaredObjects(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["properties"] is JsonObject && obj["additionalProperties"] == null)
                obj["additionalProperties"] = false;
            foreach (var child in obj.ToList()) CloseDeclaredObjects(child.Value);
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array) CloseDeclaredObjects(child);
        }
    }

    private static string Canonicalize(string name) => name.Replace("_", string.Empty).ToLowerInvariant();

    private static bool BranchMatches(JsonElement branch, JsonElement value, string path)
    {
        var branchErrors = new List<string>();
        ValidateNode(branch, value, path, branchErrors);
        return branchErrors.Count == 0;
    }

    private static void AddError(List<string> errors, string error)
    {
        if (errors.Count < MaxErrors) errors.Add(error);
    }
}
