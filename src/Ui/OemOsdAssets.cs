using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace XiControl.Ui;

/// <summary>
/// Необязательные локальные PNG штатного Xiaomi OSD. Имена сохраняются дословно;
/// выбор повторяет OEM: семейство → доступный английский вариант → Light/Dark →
/// ближайший DPI 100–500%. В официальной сборке ресурсов нет, поэтому отсутствие
/// семейства является штатным результатом, а не исключением.
/// </summary>
public static class OemOsdAssets
{
    private const string Prefix = "oem.osd.";
    private static readonly Assembly Assembly = typeof(OemOsdAssets).Assembly;
    private static readonly HashSet<string> Names = Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal) &&
            name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        .Select(name => name[Prefix.Length..])
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static int ResourceCount => Names.Count;

    /// <summary>Имя встроенного файла либо null, если локальный набор не содержит семейство.</summary>
    public static string? Resolve(string family, int dpi, bool dark, bool preferEnglish) =>
        Resolve(Names, family, dpi, dark, preferEnglish);

    /// <summary>Загрузить независимую копию Bitmap. false означает «показать свой OSD».</summary>
    public static bool TryLoad(string family, int dpi, bool dark, bool preferEnglish,
        [NotNullWhen(true)] out Bitmap? image)
    {
        image = null;
        if (Resolve(family, dpi, dark, preferEnglish) is not string file) return false;
        try
        {
            using Stream? stream = Assembly.GetManifestResourceStream(Prefix + file);
            if (stream is null) return false;
            using var source = new Bitmap(stream);
            image = new Bitmap(source); // после копии поток можно закрыть
            return true;
        }
        catch (Exception ex)
        {
            Log.Ex($"OemOsdAssets.{family}", ex);
            image?.Dispose();
            image = null;
            return false;
        }
    }

    /// <summary>Независимый поток для будущего WinUI BitmapImage. false → локализованный fallback.</summary>
    public static bool TryOpen(string family, int dpi, bool dark, bool preferEnglish,
        [NotNullWhen(true)] out MemoryStream? data)
    {
        data = null;
        if (Resolve(family, dpi, dark, preferEnglish) is not string file) return false;
        try
        {
            using Stream? source = Assembly.GetManifestResourceStream(Prefix + file);
            if (source is null) return false;
            data = new MemoryStream();
            source.CopyTo(data);
            data.Position = 0;
            return true;
        }
        catch (Exception ex)
        {
            Log.Ex($"OemOsdAssets.{family}", ex);
            data?.Dispose();
            data = null;
            return false;
        }
    }

    internal static string? Resolve(IReadOnlySet<string> names, string family, int dpi,
        bool dark, bool preferEnglish)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        family = Path.GetFileNameWithoutExtension(family.Trim());

        string stem = preferEnglish && HasStem(names, family + "_En") ? family + "_En" : family;
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
        return candidates.FirstOrDefault(names.Contains);
    }

    internal static int NormalizeScale(int dpi)
    {
        double percent = Math.Max(96, dpi) * 100d / 96d;
        return Math.Clamp((int)(Math.Round(percent / 25d, MidpointRounding.AwayFromZero) * 25), 100, 500);
    }

    /// <summary>Физические пиксели @scale переводятся в DIPs без двойного DPI-масштабирования.</summary>
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

    private static bool HasStem(IReadOnlySet<string> names, string stem) => names.Any(name =>
        name.Equals(stem + ".png", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase));
}
