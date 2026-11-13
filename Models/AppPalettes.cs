using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.Models;

/// <summary>One catalogue for settings, configuration validation and rendering. Colours are
/// opaque sRGB values; the UI adapter owns brushes, component aliases and derived states.</summary>
public static class AppPalettes
{
    public sealed record Palette(string Canvas, string Surface, string Raised,
        string Text, string Secondary, string Muted, string Accent);
    public sealed record Scheme(string Name, Palette Dark, Palette Light);

    public static IReadOnlyList<Scheme> All { get; } = Array.AsReadOnly<Scheme>(
    [
        new("Default",
            new("#16161A", "#232429", "#2C2D33", "#F2F2F4", "#CDD0D8", "#A8ADB8", "#D2D5DE"),
            new("#FFFFFF", "#F4F5F7", "#EAECF0", "#20232B", "#454B58", "#5E6572", "#303746")),
        new("Solarized",
            new("#002B36", "#073642", "#0A4653", "#F3EFDF", "#CAD8D8", "#B0C4C5", "#8DD2DB"),
            new("#FDF6E3", "#F5EEDB", "#EEE8D5", "#344C52", "#435D63", "#4C6065", "#006477")),
        new("Cyberpunk",
            new("#151126", "#211936", "#302344", "#F7EBFF", "#E0CBEA", "#C3B0CF", "#F6A1E2"),
            new("#FBF5FF", "#F4EAFB", "#EDE0F6", "#382344", "#5B4269", "#705578", "#863D7A")),
        new("Tokyo",
            new("#1A1B26", "#24283B", "#2F354D", "#E3E9FF", "#C8D2F0", "#AFBBDD", "#A9C0FF"),
            new("#EFF1FA", "#E5E9F5", "#DCE2F1", "#293552", "#405071", "#53617D", "#3C5395")),
        new("Monokai",
            new("#272822", "#33342C", "#404136", "#F8F8F2", "#DEDEC9", "#C5C5AC", "#C8E487"),
            new("#FAFAF2", "#F0F0E3", "#E5E6D5", "#34382A", "#4C5340", "#5E654D", "#4F6926")),
        new("Nord",
            new("#242C3A", "#2E3949", "#3B485B", "#ECEFF4", "#D5DFEA", "#BBCADB", "#AFDEEB"),
            new("#F5F8FC", "#EAF0F7", "#DCE6F1", "#26394C", "#40556C", "#51657A", "#356681")),
        new("Forest",
            new("#142820", "#1E352A", "#2A4336", "#E5F2E8", "#CDDFD1", "#AFC9B7", "#A5D9B2"),
            new("#F5F9F3", "#EAF2E7", "#DFEBDC", "#233C2B", "#3D5844", "#526956", "#326646")),
        new("Rose",
            new("#2A1D28", "#382A36", "#473645", "#FAEDF4", "#E7D0DF", "#CEB4C6", "#F1B4CE"),
            new("#FDF7F9", "#F6ECF1", "#EDDFE7", "#452F40", "#63495A", "#785C6E", "#8E4268")),
        new("Sandstone",
            new("#29231D", "#373027", "#463C30", "#F5EEDF", "#E1D5C1", "#CABBA3", "#E8C491"),
            new("#FCF8F0", "#F4EDDF", "#EAE1D0", "#40372A", "#5D503E", "#6F604C", "#795726")),
    ]);

    public static IReadOnlyList<string> Names { get; } = Array.AsReadOnly(All.Select(x => x.Name).ToArray());

    public static string Normalize(string? name) => All.FirstOrDefault(
        x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))?.Name ?? "Default";

    public static Scheme Get(string? name) => All.First(x => x.Name == Normalize(name));
}
