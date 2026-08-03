using Avalonia;
using System;
using System.Collections.Generic;

namespace Athena.UI.Services;

/// <summary>
/// 按用户选择的字号档位把 App.FontSize.* 语义字号整体缩放到 Application 资源，
/// 所有通过 DynamicResource 引用这些字号的控件会即时跟随，无需逐个改动视图。
/// </summary>
public static class FontScaleService
{
    /// <summary>App.axaml 中定义的基准字号，对应"中等"档位（缩放系数 1.0）。</summary>
    private static readonly IReadOnlyDictionary<string, double> Baseline = new Dictionary<string, double>
    {
        ["App.FontSize.Micro"] = 8,
        ["App.FontSize.Tiny"] = 9,
        ["App.FontSize.Caption"] = 10,
        ["App.FontSize.Body"] = 11,
        ["App.FontSize.Subheading"] = 12,
        ["App.FontSize.Heading"] = 13,
        ["App.FontSize.Title"] = 14,
        ["App.FontSize.PageTitle"] = 15,
        ["App.FontSize.Brand"] = 16,
        ["App.FontSize.Symbol"] = 18,
    };

    /// <summary>档位 → 缩放系数（中等为基准 1.0）。未知值回退到 1.0。</summary>
    public static double ScaleFor(string? level) => level switch
    {
        "Tiny" => 0.80,
        "Small" => 0.90,
        "Medium" => 1.00,
        "Large" => 1.15,
        "Maximum" => 1.30,
        _ => 1.00,
    };

    /// <summary>把基准字号按档位缩放后写入 Application 资源，全应用即时生效。</summary>
    public static void Apply(string? level)
    {
        var resources = Application.Current?.Resources;
        if (resources == null) return;
        var scale = ScaleFor(level);
        foreach (var pair in Baseline)
        {
            // 取整到像素以保持 CJK 文字清晰；下限 7 防止过小不可读。
            resources[pair.Key] = Math.Max(7.0, Math.Round(pair.Value * scale));
        }
    }
}
