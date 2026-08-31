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
    private TouchpadEdgeGesture _gesture;
    private readonly RawTouchpadReader _reader;
    private readonly Action<int> _volume;
    private readonly Action<int> _brightness;
    private EdgeSlideScale _scale;
    private readonly IAppTimer _pump;
    private readonly object _lock = new();

    private int _pendingBrightness;   // накоплено с потока чтения, применяет воркер
    private int _pendingTaps;
    private int _level;               // кэш яркости; трогает только воркер — замок не нужен
    private volatile bool _resync = true;  // перечитать яркость: жест начался заново
    private int _busy;                // тик воркера уже идёт (WorkerTimer умеет входить повторно)

    public TouchpadEdgeSliders(AppConfig cfg, TouchpadControl pad,
        Action<int>? volume = null, Action<int>? brightness = null, IAppTimer? pump = null)
    {
        _cfg = cfg;
        _pad = pad;
        _volume = volume ?? KeyActions.VolumeStep;
        _brightness = brightness ?? BrightnessStep;
        _gesture = new TouchpadEdgeGesture(WidthFraction(cfg), EdgeSlideScale.StepFraction);
        _scale = new EdgeSlideScale(cfg.TouchpadEdgeSwipesPerRange);
        // дальше их пересобирает Reconfigure — настройки живут не только на старте
        _reader = new RawTouchpadReader(OnFrame);

        // Применение вынесено с потока чтения намеренно. Первая версия звала WMI прямо в
        // обработчике кадра: синхронный Brightness.Get на каждый шаг плюс Task.Run на каждую
        // запись. При быстром движении это десятки одновременных WMI-операций в секунду —
        // цикл сообщений забивается, пул потоков голодает, и ползунок через минуту-другую
        // просто переставал отвечать. Теперь поток чтения только копит намерение.
        _pump = pump ?? new WorkerTimer();
        _pump.Interval = PumpMs;
        _pump.Tick += Drain;
    }

    /// <summary>Как часто применяем накопленное. 50 мс — быстрее человеческого «плавно»,
    /// но на два порядка реже, чем приходят кадры касаний.</summary>
    private const int PumpMs = 50;

    /// <summary>Тачпад в системе есть — без него опция бессмысленна.</summary>
    public bool Available => _pad.Available;

    /// <summary>Поднять чтение, если фича включена. Зовётся на старте и при смене настройки.</summary>
    public void Start()
    {
        // Пересобираем ВСЕГДА, даже если фича выключена: ширина полосы и чувствительность
        // берутся из конфига, а он меняется на ходу. Первая версия строила жест и шкалу
        // один раз в конструкторе — и настройка чувствительности молча ничего не делала:
        // Apply перезапускал чтение, но считали его прежние объекты со старыми числами.
        Reconfigure();
        if (!_cfg.TouchpadEdgeSliders || !Available) return;
        _reader.Start();
        _pump.Start();
    }

    /// <summary>Остановить чтение (выключение фичи, выход).</summary>
    public void Stop()
    {
        _pump.Stop();
        _reader.Stop();
        _gesture.Reset();
        ResetPending();
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
        lock (_lock)
        {
            var result = _gesture.Update(contacts);
            if (contacts.Count == 0)
            {
                // Палец оторван: остаток шкалы к следующему жесту не относится, а кэш яркости
                // протух — в следующий раз читаем реальную (её могли сменить и мимо нас).
                // Флагом, а не записью под замком: замок берёт и воркер, а он ходит в WMI,
                // и поток чтения касаний вставал бы на нём в очередь.
                _scale.Reset();
                _resync = true;
                return;
            }
            if (result.Steps == 0) return;

            bool toBrightness = (result.Edge == TouchpadEdge.Left) != _cfg.TouchpadEdgeSwap;
            if (toBrightness) _pendingBrightness += _scale.Brightness(result.Steps);
            else _pendingTaps += _scale.VolumeTaps(result.Steps);
        }
    }

    private static double WidthFraction(AppConfig cfg)
    {
        // Доля ширины панели: сама панель у каждой модели своя, а зона задаётся в мм.
        // 139 мм — измеренная ширина TM2424; для других моделей это приближение, и оно
        // безопасно: жест всё равно ограничен той же curtain-зоной, что и подавление.
        const double PadWidthMm = 139.0;
        return NormalizeWidthMm(cfg.TouchpadEdgeWidthMm) / PadWidthMm;
    }

    // Воркер: применяем накопленное пачкой. Здесь можно ходить в WMI — поток свой.
    // WorkerTimer периодический и умеет войти повторно, если тик затянулся: WMI-запись
    // изредка занимает больше 50 мс. Пропускаем такой тик — накопленное дождётся следующего.
    private void Drain()
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return;
        try
        {
            int percent, taps;
            lock (_lock)
            {
                percent = _pendingBrightness;
                taps = _pendingTaps;
                _pendingBrightness = 0;
                _pendingTaps = 0;
            }
            if (taps != 0) _volume(taps);
            if (percent != 0) _brightness(percent);
        }
        catch (Exception ex) { Log.Ex("TouchpadEdges.Drain", ex); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>
    /// Перечитать настройки без похода в реестр: ширина полосы и чувствительность живут в
    /// конфиге, а меняются на ходу. Отдельно от <see cref="Apply"/> намеренно — смена
    /// чувствительности не трогает зоны, и гонять ради неё перезапуск узла (тачпад на секунду
    /// пропадает) было бы наказанием за движение ползунка в настройках.
    /// </summary>
    public void Reconfigure()
    {
        lock (_lock)
        {
            _gesture = new TouchpadEdgeGesture(WidthFraction(_cfg), EdgeSlideScale.StepFraction);
            _scale = new EdgeSlideScale(_cfg.TouchpadEdgeSwipesPerRange);
            _pendingBrightness = 0;
            _pendingTaps = 0;
        }
        _resync = true;
    }

    private void ResetPending()
    {
        lock (_lock)
        {
            _pendingBrightness = 0;
            _pendingTaps = 0;
            _scale.Reset();
        }
        _resync = true;
    }

    // Яркость — ручная правка: пишем БЕЗ метки Own, чтобы кривая авто-яркости училась,
    // а лимит считал это осознанным выбором человека (см. XIC-56 про метки).
    // Уровень кэшируем на время жеста: WMI-чтение на каждый шаг и было тем, что вешало
    // ползунок при быстром движении.
    private void BrightnessStep(int percent)
    {
        // _level и _resync принадлежат воркеру (Drain защищён от повторного входа), поэтому
        // замок здесь не нужен — и не должен браться: под ним стоит WMI-чтение, а тот же
        // замок берёт поток чтения касаний.
        if (_resync)
        {
            if (Brightness.Get() is not int now) return;
            _level = now;
            _resync = false;
        }
        int next = Math.Clamp(_level + percent, 0, 100);
        if (next == _level) return;
        _level = next;
        Brightness.ApplyAsUser(next);
    }

    public void Dispose()
    {
        _pump.Dispose();
        _reader.Dispose();
    }
}
