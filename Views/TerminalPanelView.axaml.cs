using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaTerminal;

namespace Athena.UI.Views;

public partial class TerminalPanelView : UserControl
{
    private TerminalControl? _terminalControl;
    private bool? _terminalIsDark;
    private string? _terminalScheme;

    public TerminalPanelView()
    {
        InitializeComponent();
        RecreateTerminalControl(IsDarkTheme());
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnActualThemeVariantChanged(object? sender, System.EventArgs e)
    {
        RecreateTerminalControl(IsDarkTheme());
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        App.ThemeChanged += OnApplicationThemeChanged;
        App.ColorSchemeChanged += OnApplicationColorSchemeChanged;
        RecreateTerminalControl(IsDarkTheme());
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        App.ThemeChanged -= OnApplicationThemeChanged;
        App.ColorSchemeChanged -= OnApplicationColorSchemeChanged;
    }

    private void OnApplicationThemeChanged(string themeName)
    {
        RecreateTerminalControl(!string.Equals(
            themeName,
            "Light",
            System.StringComparison.OrdinalIgnoreCase));
    }

    private void OnApplicationColorSchemeChanged(string _) => RecreateTerminalControl(IsDarkTheme());

    private void RecreateTerminalControl(bool isDark)
    {
        if (_terminalControl is not null && _terminalIsDark == isDark
            && _terminalScheme == App.CurrentColorScheme)
        {
            return;
        }

        if (_terminalControl is not null)
        {
            _terminalControl.ClearValue(TerminalControl.ModelProperty);
        }

        var dictionaries = App.GetColorSchemeDictionaries(App.CurrentColorScheme);
        var palette = isDark ? dictionaries.Dark : dictionaries.Light;
        var foreground = (IBrush)palette["Terminal.Foreground"]!;
        var background = (IBrush)palette["Terminal.Background"]!;
        _terminalControl = new TerminalControl
        {
            RightClickAction = RightClickAction.CopyOrPaste,
            FontFamily = "Cascadia Mono,Consolas,Menlo,monospace",
            FontSize = 12,
            CaretBrush = foreground,
            SelectionBrush = (IBrush)palette["AvaloniaTerminalSelectionBrush"]!
        };
        _terminalControl.Resources["AvaloniaTerminalColor0"] = background;
        _terminalControl.Resources["AvaloniaTerminalColor7"] = foreground;
        _terminalControl.Resources["AvaloniaTerminalColor15"] = foreground;
        _terminalControl.Bind(
            TerminalControl.ModelProperty,
            new Binding("SelectedSession.Model"));
        TerminalHost.Child = _terminalControl;
        _terminalIsDark = isDark;
        _terminalScheme = App.CurrentColorScheme;
    }

    private bool IsDarkTheme()
    {
        if (ActualThemeVariant == ThemeVariant.Dark)
        {
            return true;
        }

        if (ActualThemeVariant == ThemeVariant.Light)
        {
            return false;
        }

        return Application.Current?.RequestedThemeVariant != ThemeVariant.Light;
    }
}
