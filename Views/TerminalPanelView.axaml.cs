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
        RecreateTerminalControl(IsDarkTheme());
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        App.ThemeChanged -= OnApplicationThemeChanged;
    }

    private void OnApplicationThemeChanged(string themeName)
    {
        RecreateTerminalControl(!string.Equals(
            themeName,
            "Light",
            System.StringComparison.OrdinalIgnoreCase));
    }

    private void RecreateTerminalControl(bool isDark)
    {
        if (_terminalControl is not null && _terminalIsDark == isDark)
        {
            return;
        }

        if (_terminalControl is not null)
        {
            _terminalControl.ClearValue(TerminalControl.ModelProperty);
        }

        var foreground = isDark ? Colors.White : Colors.Black;
        var background = isDark ? Colors.Black : Colors.White;
        _terminalControl = new TerminalControl
        {
            RightClickAction = RightClickAction.CopyOrPaste,
            FontFamily = "Cascadia Mono,Consolas,Menlo,monospace",
            FontSize = 12,
            CaretBrush = new SolidColorBrush(foreground),
            SelectionBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x60, 0xA0, 0xFF))
        };
        _terminalControl.Resources["AvaloniaTerminalColor0"] = new SolidColorBrush(background);
        _terminalControl.Resources["AvaloniaTerminalColor7"] = new SolidColorBrush(foreground);
        _terminalControl.Resources["AvaloniaTerminalColor15"] = new SolidColorBrush(foreground);
        _terminalControl.Bind(
            TerminalControl.ModelProperty,
            new Binding("SelectedSession.Model"));
        TerminalHost.Child = _terminalControl;
        _terminalIsDark = isDark;
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
