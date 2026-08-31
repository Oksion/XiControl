using Microsoft.Win32;
using XiControl.Config;

namespace XiControl.SystemIntegration;

/// <summary>
/// Края тачпада как ползунки (XIC-61): палец, опустившийся в краевую полосу, вертикальным
/// движением крутит яркость (слева) и громкость (справа) — как Free Touch у Huawei.
///
/// Две половины, обе driver-free:
/// <list type="number">
/// <item>курсор в полосе гасится штатной curtain-зоной Windows (<c>SuperCurtainLeft/Right</c>,
///   те же ручки, что у мёртвой зоны снизу в <see cref="TouchpadDeadZone"/>);</item>
/// <item>сам жест читается через Raw Input (<see cref="RawTouchpadReader"/>) — контакт внутри
///   зоны туда доходит, это <b>измерено</b>: 12447 касаний из левых 10% ширины при активной
///   30-миллиметровой зоне. Фильтрация живёт в PTP-маппере Windows, выше HID-драйвера.</item>
/// </list>
///
/// Мёртвой зоне снизу мы не мешаем и она нам: значения в реестре разные
/// (<c>SuperCurtainBottom</c> против <c>Left</c>/<c>Right</c>), общий у них только перезапуск
/// узла тачпада — и он идемпотентен.
///
/// Яркость меняется <b>как ручная правка человека</b>, а не как наша запись: ползунок — это
/// он и есть, поэтому кривая авто-яркости обязана на нём учиться, а лимит — считать его
/// осознанным выбором. Иначе вышло бы, что палец крутит яркость, а через минуту она уезжает
/// обратно, и виноваты в этом мы.
/// </summary>
public sealed class TouchpadEdgeSliders : IDisposable
{
    private const string RegKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\PrecisionTouchPad";
    private const string LeftValue = "SuperCurtainLeft", RightValue = "SuperCurtainRight";

    /// <summary>Ширина полосы по умолчанию, мм. Панель TM2424 — 139 мм по горизонтали
    /// (посчитано из дескриптора: 5,47 дюйма), так что 12 мм — меньше десятой части.</summary>
    public const int DefaultWidthMm = 12;

    /// <summary>Потолок ширины: измерено, что заведомо большое значение (150 мм) PTP-маппер
    /// игнорирует ВОВСЕ — курсор ездит как без зоны. Молча неработающая фича хуже узкой,
    /// поэтому не даём выставить столько.</summary>
    public static int NormalizeWidthMm(int mm) => Math.Clamp(mm, 4, 30);

    private readonly AppConfig _cfg;
    private readonly TouchpadControl _pad;
    private readonly TouchpadEdgeGesture _gesture;
    private readonly RawTouchpadReader _reader;
    private readonly Action<int> _volume;
    private readonly Action<int> _brightness;
    private readonly object _lock = new();

    public TouchpadEdgeSliders(AppConfig cfg, TouchpadControl pad,
        Action<int>? volume = null, Action<int>? brightness = null)
    {
        _cfg = cfg;
        _pad = pad;
        _volume = volume ?? KeyActions.VolumeStep;
        _brightness = brightness ?? BrightnessStep;
        _gesture = new TouchpadEdgeGesture(WidthFraction(cfg), cfg.TouchpadEdgeStepPercent / 100.0);
        _reader = new RawTouchpadReader(OnFrame);
    }

    /// <summary>Тачпад в системе есть — без него опция бессмысленна.</summary>
    public bool Available => _pad.Available;

    /// <summary>Поднять чтение, если фича включена. Зовётся на старте и при смене настройки.</summary>
    public void Start()
    {
        if (!_cfg.TouchpadEdgeSliders || !Available) return;
        _gesture.Reset();
        _reader.Start();
    }

    /// <summary>Остановить чтение (выключение фичи, выход).</summary>
    public void Stop()
    {
        _reader.Stop();
        _gesture.Reset();
    }

    /// <summary>
    /// Применить состояние из конфига: записать или убрать боковые зоны и перезапустить узел,
    /// затем поднять или опустить чтение. Как и у мёртвой зоны, зовётся ТОЛЬКО по явному
    /// переключению пользователем — на старте реестр не трогаем (CLAUDE.md).
    /// </summary>
    public bool Apply()
    {
        if (!Write()) return false;
        if (!_pad.Restart())
            Log.Write("TouchpadEdges: узел не перезапустился — зоны применятся после перезахода в сеанс");

        Stop();
        Start();
        return true;
    }

    private bool Write()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(RegKey);
            if (key is null) { Log.Write($"TouchpadEdges: не открыть HKLM\\{RegKey}"); return false; }

            if (_cfg.TouchpadEdgeSliders)
            {
                int mm = NormalizeWidthMm(_cfg.TouchpadEdgeWidthMm);
                int himetric = mm * 100;
                key.SetValue(LeftValue, himetric, RegistryValueKind.DWord);
                key.SetValue(RightValue, himetric, RegistryValueKind.DWord);
                Log.Write($"TouchpadEdges: полосы {mm} мм ({himetric} himetric)");
            }
            else
            {
                // выключение — УДАЛЕНИЕ значений, а не запись нуля: не оставляем за собой мусор
                key.DeleteValue(LeftValue, throwOnMissingValue: false);
                key.DeleteValue(RightValue, throwOnMissingValue: false);
                Log.Write("TouchpadEdges: полосы выключены (значения удалены)");
            }
            return true;
        }
        catch (Exception ex) { Log.Ex("TouchpadEdges.Write", ex); return false; }
    }

    // Кадр касаний приходит с потока читателя — сотни раз в секунду. Здесь только чистое
    // решение и вызов действия; ничего тяжёлого, иначе очередь сообщений начнёт отставать.
    private void OnFrame(IReadOnlyList<TouchContact> contacts)
    {
        (TouchpadEdge Edge, int Steps) result;
        lock (_lock) result = _gesture.Update(contacts);
        if (result.Steps == 0) return;

        bool swap = _cfg.TouchpadEdgeSwap;
        bool brightness = (result.Edge == TouchpadEdge.Left) != swap;
        if (brightness) _brightness(result.Steps); else _volume(result.Steps);
    }

    private static double WidthFraction(AppConfig cfg)
    {
        // Доля ширины панели: сама панель у каждой модели своя, а зона задаётся в мм.
        // 139 мм — измеренная ширина TM2424; для других моделей это приближение, и оно
        // безопасно: жест всё равно ограничен той же curtain-зоной, что и подавление.
        const double PadWidthMm = 139.0;
        return NormalizeWidthMm(cfg.TouchpadEdgeWidthMm) / PadWidthMm;
    }

    // Шаг яркости — ручная правка: пишем БЕЗ метки Own, чтобы кривая авто-яркости училась,
    // а лимит считал это осознанным выбором человека (см. XIC-56 про метки).
    private static void BrightnessStep(int steps)
    {
        if (Brightness.Get() is not int now) return;
        int next = Math.Clamp(now + (steps * 5), 0, 100);
        if (next != now) Brightness.ApplyAsUser(next);
    }

    public void Dispose()
    {
        _reader.Dispose();
    }
}
