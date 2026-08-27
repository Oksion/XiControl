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
    private static readonly object Sync = new();
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
    public const string PerfEco = "perf-eco";                // лист (экономия)
    public const string PerfFull = "perf-full";
    public const string PerfFullBody = "perf-full-body";     // ракета без пламени
    public const string PerfFullFlame = "perf-full-flame";   // пламя отдельно
    public const string PerfQuietMoon = "perf-quiet-moon";   // луна без звёзд
    public const string PerfQuietStar1 = "perf-quiet-star1"; // звёзды по одной —
    public const string PerfQuietStar2 = "perf-quiet-star2"; //   мерцают в противофазе
    public const string PerfQuiet = "perf-quiet";            // луна со звёздами (тишина)
    public const string PerfTurbo = "perf-turbo";
    public const string RefreshRate = "refresh-rate";        // монитор со стрелками — авто-герцовка вкл
    public const string RefreshRateOff = "refresh-rate-off"; // то же серым — авто-герцовка выкл
    public const string Settings = "settings";
    public const string FnLockOn = "fn-lock-on";
    public const string FnLockOff = "fn-lock-off";
    public const string CapsLockOn = "caps-lock-on";
    public const string CapsLockOff = "caps-lock-off";
    public const string OwlAwake = "owl-awake";   // «не спать» включён
    public const string OwlAsleep = "owl-asleep"; // «не спать» выключен

    // Трей (монохром, currentColor, 24×24)
    public const string TrayPerfEco = "tray-perf-eco";
    public const string TrayPerfFull = "tray-perf-full";
    public const string TrayPerfQuiet = "tray-perf-quiet";
    public const string TrayPerfTurbo = "tray-perf-turbo";
    public const string TraySettings = "tray-settings";
    public const string TrayLanguage = "tray-language"; // «文A» для пункта выбора языка в меню

    // Меню трея (монохром, currentColor, 24×24)
    public const string MenuBattery = "menu-battery";
    public const string MenuTravel = "menu-travel";
    public const string MenuOwl = "menu-owl";
    public const string MenuRefreshRate = "menu-refresh-rate";
    public const string MenuMonitor = "menu-monitor";
    public const string MenuPerformance = "menu-performance";
    public const string MenuPerfEco = "menu-perf-eco";
    public const string MenuPerfQuiet = "menu-perf-quiet";
    public const string MenuPerfAuto = "menu-perf-auto";
    public const string MenuPerfBalance = "menu-perf-balance";
    public const string MenuPerfTurbo = "menu-perf-turbo";
    public const string MenuPerfFull = "menu-perf-full";
    public const string MenuSettings = "menu-settings";
    public const string MenuExit = "menu-exit";

    // Широкие картинки (не иконки): кнопка «Buy me a coffee» — 545×153
    public const string BuyMeACoffee = "bmc-button";

    private static readonly Dictionary<string, string> _sources = new();       // имя → svg-текст
    private static readonly Dictionary<(string, int, int), Bitmap> _bitmaps = new(); // (имя, размер, argb-цвет|0)
    private static readonly Dictionary<(string, int), Bitmap> _wide = new();   // (имя, высота) — неквадратные

    private static string Source(string name)
    {
        lock (Sync)
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
    }

    /// <summary>Растр цветной иконки (цвета зашиты в SVG). Кэшируется, не Dispose-ить.</summary>
    public static Bitmap Render(string name, int size) => RenderCore(name, size, null);

    /// <summary>Растр монохромной иконки: currentColor → color. Кэшируется, не Dispose-ить.</summary>
    public static Bitmap Render(string name, int size, Color color) => RenderCore(name, size, color);

    /// <summary>Independent PNG stream for WinUI BitmapImage; caller may dispose it after SetSource.</summary>
    public static MemoryStream OpenPng(string name, int size, Color? color = null)
    {
        lock (Sync)
        {
            Bitmap bitmap = color is Color tint ? Render(name, size, tint) : Render(name, size);
            var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;
            return stream;
        }
    }

    /// <summary>
    /// Растр НЕквадратной картинки по заданной высоте: ширина берётся из пропорций самого SVG
    /// (иконочный <see cref="Render(string, int)"/> рисует строго в квадрат и растянул бы кнопку).
    /// Кэшируется, не Dispose-ить.
    /// </summary>
    public static Bitmap RenderByHeight(string name, int height)
    {
        lock (Sync)
        {
            if (_wide.TryGetValue((name, height), out var cached)) return cached;

            var doc = SvgDocument.FromSvg<SvgDocument>(Source(name));
            // ViewBox — источник пропорций; если его нет, берём объявленные Width/Height документа
            float w = doc.ViewBox.Width > 0 ? doc.ViewBox.Width : doc.Width.Value;
            float h = doc.ViewBox.Height > 0 ? doc.ViewBox.Height : doc.Height.Value;
            int width = h > 0 ? (int)Math.Round(height * (w / h)) : height;

            doc.Width = width;
            doc.Height = height;

            var bmp = new Bitmap(Math.Max(1, width), Math.Max(1, height));
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                doc.Draw(g);
            }
            _wide[(name, height)] = bmp;
            return bmp;
        }
    }

    private static Bitmap RenderCore(string name, int size, Color? color)
    {
        lock (Sync)
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
    }

}
