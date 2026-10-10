using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Athena.UI.Services.GameMode;

/// <summary>
/// 把一件程序化资产换成外部模型的一条覆盖。<see cref="Model"/> 是资产目录里的文件名（只有一段）；
/// 动画按动作名（walk / idle / work / deliver）指向模型里的动画片段名，缺的动作由页面退回 idle 或静止。
/// </summary>
public sealed record PolisAssetOverride(
    string Model,
    double Scale,
    double RotateY,
    IReadOnlyList<double> Offset,
    IReadOnlyDictionary<string, string> Animations);

/// <summary>读清单的结果：校验通过的覆盖，以及被拒的条目和原因（每条一个 Warning）。</summary>
public sealed record PolisAssetManifestResult(
    IReadOnlyDictionary<string, PolisAssetOverride> Assets,
    IReadOnlyList<string> Rejections);

/// <summary>
/// 资产清单（设计稿第 1 节"保留资产清单接口，任何一件都能单独换成外部模型"）。缺省时每件资产都由代码生成；
/// <c>AthenaData/Game/assets/manifest.json</c> 可以把其中任何一件换成同目录下的 glTF（.glb / .gltf）。
/// 格式见 <c>Docs/GameMode_AssetManifest_CN.md</c>。
///
/// 这里是 C# 这一道关：清单是用户放进来的文件，模型由页面按名字去回环服务取，所以名字必须是一段干净的文件名、
/// 文件必须真的在资产目录里；数字有范围；认不出的写法整条拒绝。被拒的条目不影响别的，页面对它照常用程序化的那一件——
/// 这就是"缺省回退"的 C# 一半（另一半在页面：模型加载失败同样退回）。资产 Id 只校验写法：
/// 哪些 Id 存在由页面的资产表说了算，认不出的 Id 页面忽略并记一笔。
/// </summary>
public static class PolisAssetManifest
{
    public const int Schema = 1;
    public const string FileName = "manifest.json";
    public const int MaxManifestBytes = 64 * 1024;
    public const int MaxEntries = 256;

    /// <summary>动作名：与页面的姿势表一致。</summary>
    public static IReadOnlyList<string> Actions { get; } = new[] { "walk", "idle", "work", "deliver" };

    private static readonly Regex AssetId = new(@"^(building|prop|plant|figure|civic)(\.[a-z][a-zA-Z0-9-]{0,31}){1,3}$", RegexOptions.CultureInvariant);

    /// <summary>模型文件的扩展名。glTF 的外部缓冲与贴图另见 <see cref="IsServableFileName"/>。</summary>
    private static readonly HashSet<string> ModelExtensions = new(StringComparer.OrdinalIgnoreCase) { ".glb", ".gltf" };

    /// <summary>资产目录里允许被页面取走的文件：模型本身，以及 .gltf 引用的同目录缓冲与贴图。</summary>
    private static readonly HashSet<string> ServableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".glb", ".gltf", ".bin", ".png", ".jpg", ".jpeg", ".webp", ".ktx2"
    };

    public static bool IsValidAssetId(string? id) => id != null && id.Length <= 64 && AssetId.IsMatch(id);

    /// <summary>一段干净的文件名：1–64 个字母、数字、'.'、'_'、'-'，不以 '.' 开头，带允许的扩展名。</summary>
    public static bool IsServableFileName(string? name)
        => IsCleanName(name) && ServableExtensions.Contains(Path.GetExtension(name!));

    private static bool IsCleanName(string? name)
        => !string.IsNullOrEmpty(name) && name.Length <= 64 && name[0] != '.'
           && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-');

    /// <param name="json">清单原文。</param>
    /// <param name="modelExists">资产目录里有没有这个文件（测试里是内存表）。</param>
    public static PolisAssetManifestResult Parse(string? json, Func<string, bool> modelExists)
    {
        ArgumentNullException.ThrowIfNull(modelExists);
        var assets = new Dictionary<string, PolisAssetOverride>(StringComparer.Ordinal);
        var rejections = new List<string>();
        PolisAssetManifestResult Done() => new(assets, rejections);

        if (string.IsNullOrWhiteSpace(json)) return Done();
        if (json.Length > MaxManifestBytes)
        {
            rejections.Add($"manifest is larger than {MaxManifestBytes} bytes");
            return Done();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            rejections.Add($"manifest is not JSON: {ex.Message}");
            return Done();
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt32(out var version) || version != Schema)
            {
                rejections.Add($"manifest must be an object with \"schema\": {Schema}");
                return Done();
            }
            if (!root.TryGetProperty("assets", out var entries) || entries.ValueKind != JsonValueKind.Object)
            {
                rejections.Add("manifest has no \"assets\" object");
                return Done();
            }

            foreach (var entry in entries.EnumerateObject())
            {
                if (assets.Count >= MaxEntries)
                {
                    rejections.Add($"more than {MaxEntries} entries; the rest were ignored");
                    break;
                }
                var id = entry.Name;
                if (!IsValidAssetId(id))
                {
                    rejections.Add($"'{Shorten(id)}': not an asset id (building.* / prop.* / plant.* / figure.* / civic.*)");
                    continue;
                }
                var value = entry.Value;
                if (value.ValueKind != JsonValueKind.Object)
                {
                    rejections.Add($"{id}: entry must be an object");
                    continue;
                }
                var model = value.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                if (!IsCleanName(model) || !ModelExtensions.Contains(Path.GetExtension(model!)))
                {
                    rejections.Add($"{id}: \"model\" must be a plain .glb or .gltf file name in the assets folder");
                    continue;
                }
                if (!modelExists(model!))
                {
                    rejections.Add($"{id}: model file '{model}' is not in the assets folder; the procedural asset stays");
                    continue;
                }
                if (!TryNumber(value, "scale", 1, 0.01, 100, out var scale)
                    || !TryNumber(value, "rotateY", 0, -360, 360, out var rotateY))
                {
                    rejections.Add($"{id}: \"scale\" must be in (0.01, 100] and \"rotateY\" in [-360, 360] degrees");
                    continue;
                }
                if (!TryOffset(value, out var offset))
                {
                    rejections.Add($"{id}: \"offset\" must be [x, y, z] with each value within ±50");
                    continue;
                }
                if (!TryAnimations(value, out var animations, out var why))
                {
                    rejections.Add($"{id}: {why}");
                    continue;
                }
                assets[id] = new PolisAssetOverride(model!, scale, rotateY, offset, animations);
            }
        }
        return Done();
    }

    private static bool TryNumber(JsonElement value, string name, double fallback, double min, double max, out double result)
    {
        result = fallback;
        if (!value.TryGetProperty(name, out var raw)) return true;
        if (raw.ValueKind != JsonValueKind.Number || !raw.TryGetDouble(out var d) || !double.IsFinite(d)) return false;
        if (name == "scale" ? d <= min || d > max : d < min || d > max) return false;
        result = d;
        return true;
    }

    private static bool TryOffset(JsonElement value, out IReadOnlyList<double> offset)
    {
        offset = new double[] { 0, 0, 0 };
        if (!value.TryGetProperty("offset", out var raw)) return true;
        if (raw.ValueKind != JsonValueKind.Array || raw.GetArrayLength() != 3) return false;
        var parts = new double[3];
        var i = 0;
        foreach (var part in raw.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Number || !part.TryGetDouble(out var d) || !double.IsFinite(d) || Math.Abs(d) > 50) return false;
            parts[i++] = d;
        }
        offset = parts;
        return true;
    }

    private static bool TryAnimations(JsonElement value, out IReadOnlyDictionary<string, string> animations, out string? why)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        animations = map;
        why = null;
        if (!value.TryGetProperty("animations", out var raw)) return true;
        if (raw.ValueKind != JsonValueKind.Object)
        {
            why = "\"animations\" must map walk / idle / work / deliver to clip names";
            return false;
        }
        foreach (var clip in raw.EnumerateObject())
        {
            if (!Actions.Contains(clip.Name))
            {
                why = $"unknown action '{Shorten(clip.Name)}' in \"animations\" (walk / idle / work / deliver)";
                return false;
            }
            var name = clip.Value.ValueKind == JsonValueKind.String ? clip.Value.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name.Any(char.IsControl))
            {
                why = $"clip name for '{clip.Name}' must be 1–64 printable characters";
                return false;
            }
            map[clip.Name] = name;
        }
        return true;
    }

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..39] + "…";
}
