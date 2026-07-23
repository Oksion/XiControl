using System.Reflection;
using Svg;

namespace XiControl.Ui;

/// <summary>
/// Иконки из SVG-ассетов (assets/svg, встроены в сборку как ресурсы).
/// Цветные OSD-иконки рендерятся как есть; трейные (currentColor) — с перекраской
/// под тему. Растры кэшируются по (имя × размер × цвет).
/// </summary>
public static class SvgIcons
{
    // OSD (цветные, 128×128)
    public const string BatteryCharging = "battery-charging";
    public const string BatteryChargingBody = "battery-charging-body";
    public const string BatteryChargingBolt = "battery-charging-bolt";
    public const string BatteryDischarge = "battery-discharge";
    public const string BatterySaverOn = "battery-saver-on";
    public const string BatterySaverOff = "battery-saver-off";
    public const string Travel = "travel";
    public const string Touchpad = "touchpad";
    public const string TouchpadOff = "touchpad-off";
    public const string BadgeSlow = "badge-slow";               // оверлей поверх заряда: медленный зарядник (красный «!»)
    public const string BadgeNoPd = "badge-nopd";               // оверлей поверх заряда: PD не согласован (серый «?»)
    public const string Touchscreen = "touchscreen";            // экран в рамке + тап-жест — сенсорный экран вкл
    public const string TouchscreenOff = "touchscreen-off";     // то же серым — сенсорный экран выкл
    public const string TravelOff = "travel-off";    // чемодан без молнии — «В дорогу» выключен / корпус для анимации
    public const string TravelBolt = "travel-bolt";  // молния отдельно — мигает поверх travel-off
    public const string KeyboardBacklight = "keyboard-backlight";
    public const string KeyboardBacklightOff = "keyboard-backlight-off";
    public const string KeyboardBacklight50 = "keyboard-backlight-50";
    public const string KeyboardBacklightAuto = "keyboard-backlight-auto";
    public const string MicOn = "mic-on";
    public const string MicOff = "mic-off";
    public const string PerfAuto = "perf-auto";
    public const string PerfAutoDial = "perf-auto-dial";     // спидометр без стрелки
    public const string PerfAutoNeedle = "perf-auto-needle"; // стрелка, пивот в центре
    public const string PerfEco = "perf-eco";
    public const string PerfFull = "perf-full";
    public const string PerfFullBody = "perf-full-body";     // ракета без пламени
    public const string PerfFullFlame = "perf-full-flame";   // пламя отдельно
    public const string PerfEcoMoon = "perf-eco-moon";       // луна без звёзд
    public const string PerfEcoStar1 = "perf-eco-star1";     // звёзды по одной —
    public const string PerfEcoStar2 = "perf-eco-star2";     //   мерцают в противофазе
    public const string PerfQuiet = "perf-quiet";
    public const string PerfTurbo = "perf-turbo";
    public const string RefreshRate = "refresh-rate";        // монитор со стрелками — авто-герцовка вкл
    public const string RefreshRateOff = "refresh-rate-off"; // то же серым — авто-герцовка выкл
    public const string Settings = "settings";
    public const string FnLockOn = "fn-lock-on";
    public const string FnLockOff = "fn-lock-off";
    public const string OwlAwake = "owl-awake";   // «не спать» включён
    public const string OwlAsleep = "owl-asleep"; // «не спать» выключен

    // Трей (монохром, currentColor, 24×24)
    public const string TrayPerfEco = "tray-perf-eco";
    public const string TrayPerfFull = "tray-perf-full";
    public const string TrayPerfQuiet = "tray-perf-quiet";
    public const string TrayPerfTurbo = "tray-perf-turbo";
    public const string TraySettings = "tray-settings";
    public const string TrayLanguage = "tray-language"; // «文A» для пункта выбора языка в меню

    private static readonly Dictionary<string, string> _sources = new();       // имя → svg-текст
    private static readonly Dictionary<(string, int, int), Bitmap> _bitmaps = new(); // (имя, размер, argb-цвет|0)

    private static string Source(string name)
    {
        if (_sources.TryGetValue(name, out var src)) return src;

        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("svg." + name + ".svg")
            ?? throw new FileNotFoundException($"SVG-ресурс не найден: {name}");
        using var reader = new StreamReader(stream);
        src = reader.ReadToEnd();
        _sources[name] = src;
        return src;
    }

    /// <summary>Растр цветной иконки (цвета зашиты в SVG). Кэшируется, не Dispose-ить.</summary>
    public static Bitmap Render(string name, int size) => RenderCore(name, size, null);

    /// <summary>Растр монохромной иконки: currentColor → color. Кэшируется, не Dispose-ить.</summary>
    public static Bitmap Render(string name, int size, Color color) => RenderCore(name, size, color);

    private static Bitmap RenderCore(string name, int size, Color? color)
    {
        var key = (name, size, color?.ToArgb() ?? 0);
        if (_bitmaps.TryGetValue(key, out var bmp)) return bmp;

        string text = Source(name);
        if (color is Color c)
            text = text.Replace("currentColor", $"#{c.R:X2}{c.G:X2}{c.B:X2}");

        var doc = SvgDocument.FromSvg<SvgDocument>(text);
        doc.Width = size;
        doc.Height = size;

        bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            doc.Draw(g);
        }
        _bitmaps[key] = bmp;
        return bmp;
    }

    // ================= анимированные иконки =================
    // t — время в секундах, k — амплитуда 0..1 (в панели растёт вместе с hover).
    // Всё — трансформации кэшированных битмапов, кэш при анимации не растёт.

    private static RectangleF CenteredDest(RectangleF r, int size) =>
        new(r.X + (r.Width - size) / 2f, r.Y + (r.Height - size) / 2f, size, size);

    // Единый фильтр отрисовки: прозрачность × яркость × насыщенность. Десатурация — по весам
    // Rec.709, воспринимаемая яркость не меняется (нужна светлой теме: полупрозрачные цветные
    // иконки на светлом фоне выглядят белёсыми, приглушение насыщенности — нет).
    private static System.Drawing.Imaging.ColorMatrix Fx(float alpha, float brightness, float saturation)
    {
        float s = Math.Clamp(saturation, 0f, 1f);
        float sr = (1f - s) * 0.2126f, sg = (1f - s) * 0.7152f, sb = (1f - s) * 0.0722f;
        float b = brightness;
        return new()
        {
            Matrix00 = (sr + s) * b,
            Matrix01 = sr * b,
            Matrix02 = sr * b,
            Matrix10 = sg * b,
            Matrix11 = (sg + s) * b,
            Matrix12 = sg * b,
            Matrix20 = sb * b,
            Matrix21 = sb * b,
            Matrix22 = (sb + s) * b,
            Matrix33 = Math.Clamp(alpha, 0f, 1f),
            Matrix44 = 1f,
        };
    }

    private static void DrawBitmap(Graphics g, Bitmap bmp, RectangleF dest, float alpha = 1f, float brightness = 1f, float saturation = 1f)
    {
        var pts = new[] { new PointF(dest.X, dest.Y), new PointF(dest.Right, dest.Y), new PointF(dest.X, dest.Bottom) };
        var src = new RectangleF(0, 0, bmp.Width, bmp.Height);
        if (alpha >= 0.999f && Math.Abs(brightness - 1f) < 0.001f && saturation >= 0.999f)
        {
            g.DrawImage(bmp, pts, src, GraphicsUnit.Pixel);
            return;
        }
        using var attrs = new System.Drawing.Imaging.ImageAttributes();
        attrs.SetColorMatrix(Fx(alpha, brightness, saturation));
        g.DrawImage(bmp, pts, src, GraphicsUnit.Pixel, attrs);
    }

    /// <summary>Лист (Тихий): покачивание вокруг основания черешка, как от ветерка.</summary>
    public static void DrawLeafSway(Graphics g, RectangleF r, float t, float k, float opacity = 1f, float saturation = 1f)
    {
        int size = (int)Math.Round(Math.Min(r.Width, r.Height));
        var dest = CenteredDest(r, size);
        float ang = 4.5f * k * MathF.Sin(t * 1.5f);
        float px = dest.X + dest.Width * 0.25f, py = dest.Y + dest.Height * 0.84f; // основание черешка
        var st = g.Save();
        g.TranslateTransform(px, py); g.RotateTransform(ang); g.TranslateTransform(-px, -py);
        DrawBitmap(g, Render(PerfQuiet, size), dest, opacity, 1f, saturation);
        g.Restore(st);
    }

    /// <summary>Молния (Турбо): пульсация яркости.</summary>
    public static void DrawBoltPulse(Graphics g, RectangleF r, float t, float k, float opacity = 1f, float saturation = 1f)
    {
        int size = (int)Math.Round(Math.Min(r.Width, r.Height));
        float b = 1f + 0.28f * k * (0.5f + 0.5f * MathF.Sin(t * 3.2f));
        DrawBitmap(g, Render(PerfTurbo, size), CenteredDest(r, size), opacity, b, saturation);
    }

    /// <summary>Ракета (Полная): микротряска корпуса + подрагивающее пламя.</summary>
    public static void DrawRocket(Graphics g, RectangleF r, float t, float k, float opacity = 1f, float saturation = 1f)
    {
        int size = (int)Math.Round(Math.Min(r.Width, r.Height));
        var dest = CenteredDest(r, size);

        // пламя: пульс масштаба вокруг точки крепления к соплу + мерцание
        float fs = 1f + 0.08f * k * MathF.Sin(t * 4.5f);
        float fa = 1f - 0.18f * k * (0.5f + 0.5f * MathF.Sin(t * 6f + 1f));
        float ax = dest.X + dest.Width * 0.375f, ay = dest.Y + dest.Height * 0.66f;
        var st = g.Save();
        g.TranslateTransform(ax, ay); g.ScaleTransform(fs, fs); g.TranslateTransform(-ax, -ay);
        DrawBitmap(g, Render(PerfFullFlame, size), dest, opacity * fa, 1f, saturation);
        g.Restore(st);

        // корпус: едва заметное плавное покачивание
        float sh = 0.006f * size * k;
        var body = dest;
        body.Offset(sh * MathF.Sin(t * 5f), sh * MathF.Sin(t * 7f + 2f));
        DrawBitmap(g, Render(PerfFullBody, size), body, opacity, 1f, saturation);
    }

    /// <summary>Луна (Эко): звёзды мерцают в противофазе.</summary>
    public static void DrawMoonTwinkle(Graphics g, RectangleF r, float t, float k, float opacity = 1f, float saturation = 1f)
    {
        int size = (int)Math.Round(Math.Min(r.Width, r.Height));
        var dest = CenteredDest(r, size);
        DrawBitmap(g, Render(PerfEcoMoon, size), dest, opacity, 1f, saturation);
        float a1 = 1f - 0.7f * k * (0.5f + 0.5f * MathF.Sin(t * 2.1f));
        float a2 = 1f - 0.7f * k * (0.5f + 0.5f * MathF.Sin(t * 2.1f + 2.2f));
        DrawBitmap(g, Render(PerfEcoStar1, size), dest, opacity * a1, 1f, saturation);
        DrawBitmap(g, Render(PerfEcoStar2, size), dest, opacity * a2, 1f, saturation);
    }

    /// <summary>Батарея на зарядке: молния внутри мягко пульсирует.</summary>
    public static void DrawChargingPulse(Graphics g, RectangleF r, float t, float opacity = 1f)
    {
        int size = (int)Math.Round(Math.Min(r.Width, r.Height));
        var dest = CenteredDest(r, size);
        DrawBitmap(g, Render(BatteryChargingBody, size), dest, opacity);
        float a = 0.55f + 0.45f * (0.5f + 0.5f * MathF.Sin(t * 4.2f));
        DrawBitmap(g, Render(BatteryChargingBolt, size), dest, opacity * a);
    }

    /// <summary>«В дорогу» (заряд до 100%): жёлтый чемодан статичен, молния на нём мигает.</summary>
    public static void DrawTravelPulse(Graphics g, RectangleF r, float t, float opacity = 1f)
    {
        int size = (int)Math.Round(Math.Min(r.Width, r.Height));
        var dest = CenteredDest(r, size);
        DrawBitmap(g, Render(TravelOff, size), dest, opacity);          // корпус без молнии
        float a = 0.2f + 0.8f * (0.5f + 0.5f * MathF.Sin(t * 4.5f));    // молния мигает
        DrawBitmap(g, Render(TravelBolt, size), dest, opacity * a);
    }

    /// <summary>
    /// Спидометр с поворачиваемой стрелкой: циферблат статично + стрелка под углом
    /// angleDeg (0 = как в исходной иконке) вокруг центра. Для анимации «настройки».
    /// </summary>
    public static void DrawGauge(Graphics g, RectangleF r, float angleDeg, float opacity = 1f, float saturation = 1f)
    {
        Draw(g, PerfAutoDial, r, opacity, saturation);

        int size = (int)Math.Round(Math.Min(r.Width, r.Height));
        var needle = Render(PerfAutoNeedle, size);
        var state = g.Save();
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        g.TranslateTransform(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        g.RotateTransform(angleDeg);
        var dest = new Rectangle(-size / 2, -size / 2, size, size);
        if (opacity >= 0.999f && saturation >= 0.999f)
        {
            g.DrawImage(needle, dest);
        }
        else
        {
            using var attrs = new System.Drawing.Imaging.ImageAttributes();
            attrs.SetColorMatrix(Fx(opacity, 1f, saturation));
            g.DrawImage(needle, dest, 0, 0, needle.Width, needle.Height, GraphicsUnit.Pixel, attrs);
        }
        g.Restore(state);
    }

    /// <summary>Нарисовать иконку с прозрачностью и/или приглушенной насыщенностью
    /// (эффекты неактивных состояний; см. Fx).</summary>
    public static void Draw(Graphics g, string name, RectangleF r, float opacity = 1f, float saturation = 1f)
    {
        int size = (int)Math.Round(Math.Min(r.Width, r.Height));
        var bmp = Render(name, size);
        var dest = new Rectangle((int)(r.X + (r.Width - size) / 2), (int)(r.Y + (r.Height - size) / 2), size, size);

        if (opacity >= 0.999f && saturation >= 0.999f)
        {
            g.DrawImage(bmp, dest);
            return;
        }
        using var attrs = new System.Drawing.Imaging.ImageAttributes();
        attrs.SetColorMatrix(Fx(opacity, 1f, saturation));
        g.DrawImage(bmp, dest, 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, attrs);
    }
}
