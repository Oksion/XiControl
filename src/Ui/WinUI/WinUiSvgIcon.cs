using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinUIImage = Microsoft.UI.Xaml.Controls.Image;

namespace XiControl.Ui;

/// <summary>Loads XiControl's embedded SVG artwork into reusable WinUI image sources.</summary>
internal static class WinUiSvgIcon
{
    private static readonly Dictionary<(string Name, int Pixels, int Argb), ImageSource> Sources = new();

    public static WinUIImage Create(string name, double size, int renderPixels = 96, Color? tint = null)
    {
        return new WinUIImage
        {
            Source = Source(name, renderPixels, tint),
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
        };
    }

    public static ImageSource Source(string name, int renderPixels = 96, Color? tint = null)
    {
        var key = (name, renderPixels, tint?.ToArgb() ?? 0);
        if (Sources.TryGetValue(key, out ImageSource? cached)) return cached;

        using MemoryStream stream = SvgIcons.OpenPng(name, renderPixels, tint);
        using var random = stream.AsRandomAccessStream();
        var source = new BitmapImage();
        source.SetSource(random);
        Sources[key] = source;
        return source;
    }
}
