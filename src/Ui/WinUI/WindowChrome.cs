using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace XiControl.Ui;

/// <summary>Shared WinUI command buttons for client-drawn title areas.</summary>
internal static class WindowChrome
{
    internal const string MinimizeGlyph = "\uE921";
    internal const string MaximizeGlyph = "\uE922";
    internal const string RestoreGlyph = "\uE923";
    internal const string CloseGlyph = "\uE8BB";

    public static Button Button(string glyph, string tooltip, Action action, bool close = false)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = 12 };
        var button = new Button
        {
            Content = icon,
            Width = 40,
            Height = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(WinUiRadii.Control),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            AllowFocusOnInteraction = false,
        };
        button.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(close
            ? Windows.UI.Color.FromArgb(255, 196, 43, 55)
            : Windows.UI.Color.FromArgb(24, 96, 112, 128));
        button.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(close
            ? Windows.UI.Color.FromArgb(255, 166, 32, 44)
            : Windows.UI.Color.FromArgb(38, 96, 112, 128));
        if (close)
        {
            var white = new SolidColorBrush(Microsoft.UI.Colors.White);
            button.Resources["ButtonForegroundPointerOver"] = white;
            button.Resources["ButtonForegroundPressed"] = white;
        }
        SetTooltip(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    public static void SetGlyph(Button button, string glyph)
    {
        if (button.Content is FontIcon icon) icon.Glyph = glyph;
    }

    public static void SetTooltip(Button button, string tooltip)
    {
        ToolTipService.SetToolTip(button, tooltip);
        AutomationProperties.SetName(button, tooltip);
    }
}
