using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using System.Collections.Generic;

namespace Athena.UI.Views;

/// <summary>
/// 配色方案缩略图：mockup 所需键从方案字典强制解析后装入控件自身的 ThemeDictionaries，
/// 明暗切换时随 ActualThemeVariant 自动换色，永远显示该方案在当前明暗模式下的真实配色。
/// 注意：不能直接挂载 App 的字典实例（Avalonia 的 ResourceDictionary 是独占单父宿主），
/// 这里克隆为独立实例；方案字典缺的键（如 Default 无 SemiColorBackground0）回落 SemiTheme 内置默认值。
/// </summary>
public partial class ColorSchemeThumbnailView : UserControl
{
    /// <summary>mockup 使用的全部资源键（其余键缩略图用不到，无需解析）。</summary>
    private static readonly string[] ThumbnailKeys =
    [
        "SemiColorBackground0", "SemiColorBackground1", "SemiColorBackground2",
        "SemiColorText0", "SemiColorText2",
        "SemiColorPrimary", "SemiColorPrimaryLight", "SemiColorBorder",
    ];

    /// <summary>SemiTheme 内置调色板（Dark），供方案字典缺键时回落（即 Default 方案的真实外观）。</summary>
    private static readonly IReadOnlyDictionary<string, string> DarkFallbacks =
        new Dictionary<string, string>
        {
            ["SemiColorBackground0"] = "#FF16161A",
            ["SemiColorBackground1"] = "#FF232429",
            ["SemiColorBackground2"] = "#FF35363C",
            ["SemiColorText0"] = "#FFF9F9F9",
            ["SemiColorText2"] = "#99F9F9F9",
            ["SemiColorPrimary"] = "#FF54A9FF",
            ["SemiColorPrimaryLight"] = "#3354A9FF",
            ["SemiColorBorder"] = "#14FFFFFF",
        };

    /// <summary>SemiTheme 内置调色板（Light），供方案字典缺键时回落。</summary>
    private static readonly IReadOnlyDictionary<string, string> LightFallbacks =
        new Dictionary<string, string>
        {
            ["SemiColorBackground0"] = "#FFFFFFFF",
            ["SemiColorBackground1"] = "#FFFFFFFF",
            ["SemiColorBackground2"] = "#FFFFFFFF",
            ["SemiColorText0"] = "#FF1C1F23",
            ["SemiColorText2"] = "#991C1F23",
            ["SemiColorPrimary"] = "#FF0064FA",
            ["SemiColorPrimaryLight"] = "#FFEAF5FF",
            ["SemiColorBorder"] = "#141C1F23",
        };

    public static readonly StyledProperty<string> SchemeNameProperty =
        AvaloniaProperty.Register<ColorSchemeThumbnailView, string>(nameof(SchemeName), "Default");

    public string SchemeName
    {
        get => GetValue(SchemeNameProperty);
        set => SetValue(SchemeNameProperty, value);
    }

    public ColorSchemeThumbnailView()
    {
        InitializeComponent();
        // 构造函数也装载一次：SchemeName 保持默认值时不触发属性变更。
        ApplySchemeDictionaries();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SchemeNameProperty)
            ApplySchemeDictionaries();
    }

    private void ApplySchemeDictionaries()
    {
        var (dark, light) = App.GetColorSchemeDictionaries(SchemeName);
        Resources.ThemeDictionaries[ThemeVariant.Dark] = BuildThumbnailDictionary(dark, ThemeVariant.Dark);
        Resources.ThemeDictionaries[ThemeVariant.Light] = BuildThumbnailDictionary(light, ThemeVariant.Light);
    }

    private static ResourceDictionary BuildThumbnailDictionary(ResourceDictionary source, ThemeVariant variant)
    {
        var fallbacks = variant == ThemeVariant.Dark ? DarkFallbacks : LightFallbacks;
        var dict = new ResourceDictionary();
        foreach (var key in ThumbnailKeys)
        {
            if (source.TryGetResource(key, variant, out var value) && value != null)
            {
                dict[key] = value;
            }
            else if (fallbacks.TryGetValue(key, out var hex))
            {
                dict[key] = new SolidColorBrush(Color.Parse(hex));
            }
        }
        return dict;
    }
}
