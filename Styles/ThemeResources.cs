using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Athena.UI.Models;
using TextMateSharp.Grammars;

namespace Athena.UI.Styles;

/// <summary>Builds a complete light/dark resource contract from seven palette colours.
/// Component aliases live here once, so adding a scheme cannot omit text or state colours.
/// All dictionaries are cached by App and replaced together on the UI thread.</summary>
internal static class ThemeResources
{
    // Images belong to the application lifetime; all palettes share these four decodes.
    private static readonly Dictionary<string, Bitmap> Images = new();

    public static ResourceDictionary Create(AppPalettes.Palette palette, bool dark)
    {
        var result = new ResourceDictionary();
        var canvas = Color.Parse(palette.Canvas);
        var surface = Color.Parse(palette.Surface);
        var raised = Color.Parse(palette.Raised);
        var text = Color.Parse(palette.Text);
        var secondary = Color.Parse(palette.Secondary);
        var muted = Color.Parse(palette.Muted);
        var accent = Color.Parse(palette.Accent);
        var inverse = dark ? canvas : Colors.White;
        var border = Mix(surface, text, dark ? 0.23 : 0.19);
        var selected = Mix(surface, accent, dark ? 0.06 : 0.04);
        var selectedHover = Mix(surface, accent, dark ? 0.09 : 0.06);
        var selectedActive = Mix(surface, accent, dark ? 0.12 : 0.08);
        var accentHover = Mix(accent, dark ? Colors.White : Colors.Black, 0.08);
        var accentActive = Mix(accent, dark ? Colors.White : Colors.Black, 0.14);
        var disabled = Mix(surface, text, 0.35);
        var success = Color.Parse(dark ? "#B4E0BE" : "#27603B");
        var warning = Color.Parse(dark ? "#F0D19A" : "#79511D");
        var danger = Color.Parse(dark ? "#FFB8BD" : "#A3283B");

        void Brushes(Color color, params string[] keys)
        {
            var brush = new SolidColorBrush(color);
            foreach (var key in keys) result[key] = brush;
        }
        void Alias(string key, string source) => result[key] = result[source]!;

        Brushes(canvas, "App.CanvasBackground", "SemiColorBackground0", "App.PanelBackgroundBrush",
            "App.PanelVeilBrush", "App.WorkspaceLeftBackground", "Editor.Background", "Terminal.Background",
            "AvaloniaTerminalColor0");
        Brushes(surface, "App.SurfaceBackground", "App.SectionBg", "SemiColorBackground1",
            "App.WorkspaceRightBackground", "Chat.AssistantBubbleBg", "App.AssistantBubbleBg", "SemiGrey0",
            "App.NavigationCardBackgroundBrush");
        Brushes(raised, "App.RaisedBackground", "SemiColorBackground2", "SemiColorBackground3",
            "SemiColorBackground4", "SemiGrey1", "SemiColorFill0", "SemiColorFill1");
        Brushes(Mix(raised, text, 0.06), "SemiColorFill2");
        Brushes(text, "App.TextPrimary", "SemiColorText0", "App.IconForeground", "Markdown.HeadingFg",
            "Chat.UserForeground", "Chat.AssistantForeground", "Editor.Foreground", "Terminal.Foreground",
            "AvaloniaTerminalColor7", "AvaloniaTerminalColor15", "AvaloniaTerminalCaretBrush");
        Brushes(secondary, "App.TextSecondary", "SemiColorText1", "CodeBlock.ToolButtonForeground");
        // Opaque secondary text: alpha-based greys change contrast on every container.
        Brushes(muted, "App.TextMuted", "SemiColorText2", "SemiColorText3", "Chat.TimestampFg",
            "App.IconMutedForeground");
        Brushes(border, "App.SurfaceBorder", "SemiColorBorder", "SemiGreyLowBrush", "App.PanelBorderBrush",
            "App.NavigationCardBorderBrush", "App.NavigationDividerBrush");
        result["App.PanelShadow"] = default(BoxShadows);
        Brushes(accent, "App.EmphasisForeground", "App.IconAccentForeground", "App.SelectedBorderBrush",
            "App.FocusBorderBrush", "App.ToggleCheckedBackground", "SemiColorPrimary", "SemiColorSecondary",
            "SemiColorLink", "SemiColorFocusBorder", "Chat.LoadingDotBrush");
        Brushes(accentHover, "App.EmphasisPointeroverForeground", "App.ToggleCheckedPointeroverBackground",
            "SemiColorPrimaryPointerover", "SemiColorLinkHover");
        Brushes(accentActive, "App.EmphasisActiveForeground", "App.ToggleCheckedPressedBackground",
            "SemiColorPrimaryActive", "SemiColorLinkActive");
        Brushes(disabled, "App.EmphasisDisabledForeground", "SemiColorPrimaryDisabled");
        Brushes(inverse, "App.InverseForeground");
        Brushes(selected, "App.SelectedBackground", "App.AccentButtonBackground", "SemiColorPrimaryLight",
            "Chat.UserBubbleBg", "Editor.SelectionBackground", "AvaloniaTerminalSelectionBrush");
        Brushes(selectedHover, "App.SelectedBackgroundPointerover", "App.AccentButtonPointeroverBackground",
            "SemiColorPrimaryLightPointerover");
        Brushes(selectedActive, "App.SelectedBackgroundActive", "App.AccentButtonPressedBackground",
            "SemiColorPrimaryLightActive");
        Brushes(accent, "App.AccentButtonBorderBrush");
        result["App.SelectedBackgroundFaded"] = new SolidColorBrush(selected, 0);

        Brushes(success, "App.SuccessForeground", "SemiColorSuccess", "SemiGreen5", "SemiGreen6");
        Brushes(warning, "App.WarningForeground", "SemiColorWarning", "SemiOrange5", "SemiOrange6",
            "SemiYellow5", "SemiYellow6", "Chat.ArchivedFg");
        Brushes(danger, "App.DangerForeground", "App.IconDangerForeground", "SemiColorDanger", "SemiRed5", "SemiRed6");
        Brushes(Mix(surface, danger, 0.08), "App.DangerBackground");
        Brushes(Mix(surface, danger, 0.45), "App.DangerBorder");
        Brushes(Mix(canvas, success, 0.10), "Editor.AddedBackground");
        Brushes(Mix(canvas, danger, 0.10), "Editor.RemovedBackground");

        // Attention cards have their own complete foreground/background pair in both modes.
        Brushes(Mix(canvas, warning, 0.08), "History.PendingCardBackground");
        Brushes(Mix(canvas, warning, 0.12), "History.PendingCardPointeroverBackground");
        Brushes(Mix(canvas, warning, 0.16), "History.PendingCardSelectedBackground");
        Brushes(warning, "History.PendingTitleForeground", "History.PendingMetaForeground",
            "History.PendingStatusForeground", "History.PendingCardSelectedBorderBrush");
        Brushes(Mix(canvas, warning, 0.45), "History.PendingCardBorderBrush", "History.PendingSeparatorBrush");

        // Markdown expects Color values rather than brushes for these keys.
        result["ForegroundColor"] = text;
        result["BorderColor"] = border;
        result["CardBackgroundColor"] = surface;
        result["SecondaryCardBackgroundColor"] = raised;
        result["CodeInlineColor"] = accent;
        result["Markdown.CodeTheme"] = dark ? ThemeName.DarkPlus : ThemeName.LightPlus;
        result["QuoteBorderColor"] = accent;
        Brushes(border, "BorderBrush");
        Brushes(text, "ForegroundBrush");
        var overlay = dark ? Colors.White : Colors.Black;
        foreach (var (key, opacity) in new (string, double)[]
        {
            ("Background", 0), ("BorderBrush", 0), ("PointeroverBackground", 0.07),
            ("PressedBackground", 0.11), ("CheckedBackground", 0.08),
            ("CheckedPointeroverBackground", 0.11), ("CheckedPressedBackground", 0.14),
            ("CheckedBorderBrush", 0.20)
        }) result["CodeBlock.ToolButton" + key] = new SolidColorBrush(overlay, opacity);

        // Solid buttons, checked controls and selections must resolve as paired colours.
        Alias("ButtonDefaultPrimaryForeground", "App.EmphasisForeground");
        Alias("ButtonSolidForeground", "App.InverseForeground");
        Alias("ButtonSolidPrimaryBackground", "App.EmphasisForeground");
        Alias("ButtonSolidPrimaryPointeroverBackground", "App.EmphasisPointeroverForeground");
        Alias("ButtonSolidPrimaryPressedForeground", "App.InverseForeground");
        Alias("ButtonSolidPrimaryPressedBackground", "App.EmphasisActiveForeground");
        Alias("ButtonSolidPrimaryBorderBrush", "App.EmphasisForeground");
        Alias("ButtonSolidPrimaryPointeroverBorderBrush", "App.EmphasisPointeroverForeground");
        Alias("ButtonSolidPrimaryPressedBorderBrush", "App.EmphasisActiveForeground");
        Alias("ButtonInputInnerPointeroverForeground", "App.EmphasisPointeroverForeground");
        Alias("ButtonInputInnerPressedForeground", "App.EmphasisActiveForeground");
        Alias("ListBoxItemSelectedBackground", "App.SelectedBackground");
        Alias("ListBoxItemSelectedPointeroverBackground", "App.SelectedBackgroundPointerover");
        Alias("TreeViewItemSelectedBackground", "App.SelectedBackground");
        Alias("ComboBoxItemSelectedBackground", "App.SelectedBackground");
        Alias("ComboBoxItemSelectedPointeroverBackground", "App.SelectedBackgroundPointerover");
        Alias("SliderTrackForeground", "App.EmphasisForeground");
        Alias("SliderThumbBorderBrush", "App.EmphasisForeground");
        Alias("SliderThumbPointeroverBorderBrush", "App.EmphasisPointeroverForeground");
        Alias("SliderThumbPressedBorderBrush", "App.EmphasisActiveForeground");
        Alias("TextBoxFocusBorderBrush", "App.FocusBorderBrush");
        Alias("ComboBoxSelectorFocusBorderBrush", "App.FocusBorderBrush");
        Alias("CalendarDatePickerFocusBorderBrush", "App.FocusBorderBrush");
        Alias("CheckBoxPointeroverBorderBrush", "App.EmphasisForeground");
        Alias("CheckBoxPressedBorderBrush", "App.EmphasisActiveForeground");
        Alias("CheckBoxCheckedDefaultBackground", "App.EmphasisForeground");
        Alias("CheckBoxCheckedDefaultBorderBrush", "App.EmphasisForeground");
        Alias("CheckBoxCheckedPointeroverBackground", "App.EmphasisPointeroverForeground");
        Alias("CheckBoxCheckedPointeroverBorderBrush", "App.EmphasisPointeroverForeground");
        Alias("CheckBoxCheckedPressedBackground", "App.EmphasisActiveForeground");
        Alias("CheckBoxCheckedPressedBorderBrush", "App.EmphasisActiveForeground");
        Alias("CheckBoxCheckedDisabledBackground", "App.EmphasisDisabledForeground");
        Alias("CheckBoxCheckedDisabledBorderBrush", "App.EmphasisDisabledForeground");
        Alias("CheckBoxCardCheckedBackground", "App.SelectedBackground");
        Alias("CheckBoxCardCheckedBorderBrush", "App.EmphasisForeground");
        Alias("CheckBoxCardCheckedPointeroverBorderBrush", "App.EmphasisPointeroverForeground");
        Alias("CheckBoxCardCheckedPressedBorderBrush", "App.EmphasisActiveForeground");
        Alias("CheckBoxGlyphFill", "App.InverseForeground");
        Alias("RadioButtonUncheckIconPointeroverBorderBrush", "App.EmphasisPointeroverForeground");
        Alias("RadioButtonUncheckIconPressedBorderBrush", "App.EmphasisActiveForeground");
        Alias("RadioButtonCheckIconDefaultBackground", "App.EmphasisForeground");
        Alias("RadioButtonCheckIconPointeroverBackground", "App.EmphasisPointeroverForeground");
        Alias("RadioButtonCheckIconPressedBackground", "App.EmphasisActiveForeground");
        Alias("RadioButtonCheckIconDisabledBackground", "App.EmphasisDisabledForeground");
        Alias("RadioButtonCheckIconDefaultBorderBrush", "App.EmphasisForeground");
        Alias("RadioButtonCheckIconPointeroverBorderBrush", "App.EmphasisPointeroverForeground");
        Alias("RadioButtonCheckIconPressedBorderBrush", "App.EmphasisActiveForeground");
        Alias("RadioButtonCheckIconDisabledBorderBrush", "App.EmphasisDisabledForeground");
        Alias("RadioButtonCheckGlyphFill", "App.InverseForeground");
        Alias("RadioButtonButtonCheckedForeground", "App.EmphasisForeground");
        Alias("RadioButtonCardCheckedBackground", "App.SelectedBackground");
        Alias("RadioButtonCardCheckDefaultBorderBrush", "App.EmphasisForeground");
        Alias("RadioButtonCardCheckPointeroverBorderBrush", "App.EmphasisPointeroverForeground");
        Alias("RadioButtonCardCheckPressedBorderBrush", "App.EmphasisActiveForeground");
        Alias("ToggleSwitchContainerCheckedDefaultBackground", "App.ToggleCheckedBackground");
        Alias("ToggleSwitchContainerCheckedPointeroverBackground", "App.ToggleCheckedPointeroverBackground");
        Alias("ToggleSwitchContainerCheckedPressedBackground", "App.ToggleCheckedPressedBackground");
        Alias("ToggleSwitchContainerCheckedDisabledBackground", "App.EmphasisDisabledForeground");
        Alias("SimpleToggleSwitchContainerCheckedForeground", "App.InverseForeground");
        Alias("ButtonSolidPrimaryPointeroverForeground", "App.InverseForeground");
        Alias("ListBoxItemSelectedForeground", "App.TextPrimary");
        Alias("TreeViewItemSelectedForeground", "App.TextPrimary");
        Alias("ComboBoxItemSelectedForeground", "App.TextPrimary");
        Alias("TextBoxDefaultForeground", "App.TextPrimary");
        Alias("TextBoxDefaultBackground", "App.CanvasBackground");
        Alias("TextBoxSelectionBrush", "App.SelectedBackground");
        Alias("TextBoxSelectionForegroundBrush", "App.TextPrimary");
        // Semi's tab aliases otherwise retain its built-in blue after a palette switch.
        Alias("TabControlSeparatorBorderBrush", "App.SurfaceBorder");
        Alias("TabItemLinePipeSelectedBackground", "App.EmphasisForeground");
        Alias("TabItemLinePipePointeroverBorderBrush", "App.EmphasisPointeroverForeground");
        Alias("TabItemLinePipePressedBorderBrush", "App.EmphasisActiveForeground");
        Alias("TabItemLineHeaderForeground", "App.TextSecondary");
        Alias("TabItemLineHeaderPointeroverForeground", "App.EmphasisForeground");
        Alias("TabItemLineHeaderSelectedForeground", "App.EmphasisForeground");
        Alias("TabItemIconSelectedForeground", "App.EmphasisForeground");
        Alias("TabItemCardHeaderSelectedBackground", "App.CanvasBackground");
        Alias("TabItemCardHeaderPointeroverBackground", "App.SelectedBackgroundPointerover");
        Alias("TabItemCardHeaderPressedBackground", "App.SelectedBackgroundActive");
        Alias("TabItemButtonHeaderSelectedForeground", "App.EmphasisForeground");
        Alias("TabItemButtonHeaderSelectedBackground", "App.SelectedBackground");
        Alias("TabItemButtonHeaderPointeroverBackground", "App.SelectedBackgroundPointerover");
        Alias("TabItemButtonHeaderPressedBackground", "App.SelectedBackgroundActive");

        var mode = dark ? "Dark" : "Light";
        result["About.LogoImage"] = Image(mode + "Logo.webp");
        result["App.BackgroundImage"] = Image(mode + ".webp");
        return result;
    }

    private static Color Mix(Color background, Color foreground, double amount) => Color.FromRgb(
        (byte)Math.Round(background.R * (1 - amount) + foreground.R * amount),
        (byte)Math.Round(background.G * (1 - amount) + foreground.G * amount),
        (byte)Math.Round(background.B * (1 - amount) + foreground.B * amount));

    private static Bitmap Image(string name)
    {
        if (Images.TryGetValue(name, out var cached)) return cached;
        using var stream = AssetLoader.Open(new Uri("avares://Athena.UI/Assets/" + name));
        return Images[name] = new Bitmap(stream);
    }
}
