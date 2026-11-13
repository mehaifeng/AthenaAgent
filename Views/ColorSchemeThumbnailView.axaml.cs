using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace Athena.UI.Views;

/// <summary>
/// 配色方案缩略图：mockup 所需键从方案字典强制解析后装入控件自身的 ThemeDictionaries，
/// 明暗切换时随 ActualThemeVariant 自动换色，永远显示该方案在当前明暗模式下的真实配色。
/// 注意：不能直接挂载 App 的字典实例（Avalonia 的 ResourceDictionary 是独占单父宿主），
/// 这里复制到独立字典；所有预览键都必须由方案显式提供，不能回落到当前应用主题。
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
        var dict = new ResourceDictionary();
        foreach (var key in ThumbnailKeys)
        {
            if (source.TryGetResource(key, variant, out var value) && value != null)
            {
                dict[key] = value;
            }
            else
            {
                throw new System.InvalidOperationException($"Theme preview is missing {key} ({variant.Key}).");
            }
        }
        return dict;
    }
}
