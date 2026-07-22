using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Athena.UI;
using Athena.UI.ViewModels;
using Athena.UI.Views;

var outputPath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(AppContext.BaseDirectory, "main-window.png");

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions
    {
        UseHeadlessDrawing = false
    })
    .SetupWithoutStarting();

var window = new MainWindow
{
    DataContext = new MainWindowViewModel(),
    Width = 1440,
    Height = 900
};
window.Show();
Dispatcher.UIThread.RunJobs();

var shell = window.FindControl<Grid>("MainShellGrid") ?? throw new InvalidOperationException("Main shell was not created.");
if (shell.ColumnDefinitions.Count != 5) throw new InvalidOperationException("Main shell must contain five grid columns including splitters.");
if (shell.ColumnDefinitions[0].MinWidth < 260 || shell.ColumnDefinitions[4].MinWidth < 360)
    throw new InvalidOperationException("Side panel minimum widths are not applied.");
if (window.FindControl<ChatTabView>("ChatTabView") == null)
    throw new InvalidOperationException("Chat view is not permanently mounted in the center column.");
if (window.GetVisualDescendants().OfType<TabStrip>().Any())
    throw new InvalidOperationException("The main window must not contain a TabStrip.");

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Headless renderer returned no frame.");
await using (var output = File.Create(outputPath)) frame.Save(output, PngBitmapEncoderOptions.Default);
Console.WriteLine($"[PASS] main shell rendered to {outputPath}");
Console.WriteLine("[PASS] three semantic columns, two splitters, side minimum widths, permanent chat, no main TabStrip");
window.Close();

var onboardingPath = Path.Combine(Path.GetDirectoryName(outputPath)!, "athena-onboarding.png");
var onboarding = new OnboardingWindow(new OnboardingViewModel())
{
    Width = 720,
    Height = 600
};
onboarding.Show();
Dispatcher.UIThread.RunJobs();
using var onboardingFrame = onboarding.CaptureRenderedFrame() ?? throw new InvalidOperationException("Onboarding renderer returned no frame.");
await using (var output = File.Create(onboardingPath)) onboardingFrame.Save(output, PngBitmapEncoderOptions.Default);
Console.WriteLine($"[PASS] minimal onboarding rendered to {onboardingPath}");
onboarding.Close();
