using Microsoft.UI.Xaml;
using Microsoft.Win32;

namespace XiControl.Ui;

/// <summary>Определение темы Windows; сами WinUI-контролы следуют системе через RequestedTheme.</summary>
public static class Theme
{
    public static bool IsDark() => ReadLight("AppsUseLightTheme", fallback: false) is false;
    public static bool TaskbarIsLight() => ReadLight("SystemUsesLightTheme", fallback: true);

    private static bool ReadLight(string name, bool fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue(name) is int value ? value != 0 : fallback;
        }
        catch (Exception ex)
        {
            Log.Ex($"Theme.{name}", ex);
            return fallback;
        }
    }
}

public static class FlyoutPalette
{
    public static ElementTheme RequestedTheme { get; private set; } = ElementTheme.Dark;
    public static bool Dark => RequestedTheme switch
    {
        ElementTheme.Light => false,
        ElementTheme.Dark => true,
        _ => Theme.IsDark(),
    };

    public static Windows.UI.Color Card => Dark
        ? Windows.UI.Color.FromArgb(242, 30, 31, 34)
        : Windows.UI.Color.FromArgb(242, 248, 249, 251);

    public static Windows.UI.Color Border => Dark
        ? Windows.UI.Color.FromArgb(84, 255, 255, 255)
        : Windows.UI.Color.FromArgb(52, 73, 86, 108);

    public static void Apply(string? value) => RequestedTheme = value?.ToLowerInvariant() switch
    {
        "light" => ElementTheme.Light,
        "system" => ElementTheme.Default,
        _ => ElementTheme.Dark,
    };
}
