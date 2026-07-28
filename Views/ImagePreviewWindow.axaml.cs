using System.IO;
using Athena.UI.Models;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;

namespace Athena.UI.Views;

public partial class ImagePreviewWindow : Window
{
    public ImagePreviewWindow()
    {
        InitializeComponent();
        WireEvents();
    }

    public ImagePreviewWindow(ChatAttachment attachment) : this()
    {
        DataContext = attachment;

        if (!string.IsNullOrWhiteSpace(attachment.StoredPath) && File.Exists(attachment.StoredPath))
        {
            using var stream = File.OpenRead(attachment.StoredPath);
            PreviewImage.Source = new Bitmap(stream);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void WireEvents()
    {
        CloseButton.Click += (_, _) => Close();
    }
}
