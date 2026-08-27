using System.Reflection;

namespace XiControl.Ui;

/// <summary>
/// Встроенные PNG штатного Xiaomi OSD. Имена сохранены дословно; выбор повторяет OEM:
/// семейство → при наличии английский вариант → Light/Dark → ближайший DPI 100–500%.
/// </summary>
public static class OemOsdAssets
{
    public const int ExpectedResourceCount = 1758;
    private const string Prefix = "oem.osd.";
    private static readonly Assembly Assembly = typeof(OemOsdAssets).Assembly;
    private static readonly HashSet<string> Names = Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        .Select(n => n[Prefix.Length..])
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static int ResourceCount => Names.Count;

    /// <summary>Имя реально встроенного файла для темы/языка/DPI.</summary>
    public static string Resolve(string family, int dpi, bool dark, bool preferEnglish)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        family = Path.GetFileNameWithoutExtension(family.Trim());

        string stem = preferEnglish && HasStem(family + "_En") ? family + "_En" : family;
        string theme = dark ? "Dark" : "Light";
        int scale = NormalizeScale(dpi);
        string suffix = scale == 100 ? "" : $"@{scale}";

        string[] candidates =
        [
            $"{stem}_{theme}{suffix}.png",
            $"{stem}_{theme}.png",
            $"{stem}.png",
            $"{family}_{theme}{suffix}.png",
            $"{family}_{theme}.png",
            $"{family}.png",
        ];
        return candidates.FirstOrDefault(Names.Contains)
            ?? throw new FileNotFoundException($"OEM OSD family is not embedded: {family}");
    }

    public static Bitmap Load(string family, int dpi, bool dark, bool preferEnglish)
    {
        string file = Resolve(family, dpi, dark, preferEnglish);
        using var stream = Assembly.GetManifestResourceStream(Prefix + file)
            ?? throw new FileNotFoundException($"Embedded OEM OSD resource is missing: {file}");
        using var source = new Bitmap(stream);
        return new Bitmap(source); // поток можно закрыть только после независимой копии
    }

    /// <summary>Независимый поток для WinUI BitmapImage; после SetSource вызывающий может его закрыть.</summary>
    public static MemoryStream Open(string family, int dpi, bool dark, bool preferEnglish)
    {
        string file = Resolve(family, dpi, dark, preferEnglish);
        using var source = Assembly.GetManifestResourceStream(Prefix + file)
            ?? throw new FileNotFoundException($"Embedded OEM OSD resource is missing: {file}");
        var copy = new MemoryStream();
        source.CopyTo(copy);
        copy.Position = 0;
        return copy;
    }

    internal static int NormalizeScale(int dpi)
    {
        double percent = Math.Max(96, dpi) * 100d / 96d;
        return Math.Clamp((int)(Math.Round(percent / 25d, MidpointRounding.AwayFromZero) * 25), 100, 500);
    }

    /// <summary>
    /// OEM @scale images contain physical pixels, while WinUI Image width/height are DIPs.
    /// Keeping those units separate prevents high-DPI assets from being enlarged a second time.
    /// </summary>
    internal static (double WidthDips, double HeightDips, int WidthPixels, int HeightPixels)
        Layout(int sourceWidth, int sourceHeight, int dpi)
    {
        int scale = NormalizeScale(dpi);
        double widthDips = sourceWidth * 100d / scale;
        double heightDips = sourceHeight * 100d / scale;
        int effectiveDpi = Math.Max(96, dpi);
        return (
            widthDips,
            heightDips,
            Math.Max(1, (int)Math.Round(widthDips * effectiveDpi / 96d)),
            Math.Max(1, (int)Math.Round(heightDips * effectiveDpi / 96d)));
    }

    private static bool HasStem(string stem) => Names.Any(n =>
        n.Equals(stem + ".png", StringComparison.OrdinalIgnoreCase) ||
        n.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase));
}
