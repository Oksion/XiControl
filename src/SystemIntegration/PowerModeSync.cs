using System.Runtime.InteropServices;
using Microsoft.Win32;
using XiControl.Config;
using XiControl.Wmi;

namespace XiControl.SystemIntegration;

/// <summary>
/// Режим питания Windows вместе с режимом ноутбука (XIC-96) — то единственное полезное, что
/// делает служба Xiaomi MiDeviceService (docs/17). Опция, по умолчанию выключена.
///
/// «Режим питания» Windows — это overlay поверх сбалансированной схемы, и он у сети и у батареи
/// свой. <c>PowerSetActiveOverlayScheme</c> пишет только текущий источник (проверено на TM2424:
/// от сети меняется ActiveOverlayAcPowerScheme, батарейный не тронут; прав администратора не
/// нужно), поэтому на смену питания сверяемся заново.
///
/// Одна точка — <see cref="Request"/>: читаем режим из прошивки, а не берём из вызвавшего, так
/// что всё равно, кто менял режим — мы, Fn+K или профиль питания. Пишем, только если overlay
/// отличается: двойные 0x16 на TM2424 и наши повторные вызовы ничего не стоят.
///
/// Со службой не соревнуемся: пока MiDeviceService запущена, она сама пишет overlay на каждое
/// 0x16, и победил бы тот, кто записал последним. Тогда молчим (вкладка показывает тумблер
/// неактивным).
/// </summary>
public sealed class PowerModeSync : IDisposable
{
    /// <summary>«Эффективность энергопотребления» (в службе Xiaomi — «Максимальная экономия энергии»).</summary>
    public static readonly Guid Efficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");

    /// <summary>«Сбалансированный» — это отсутствие overlay.</summary>
    public static readonly Guid Balanced = Guid.Empty;

    /// <summary>«Максимальная производительность».</summary>
    public static readonly Guid Performance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    /// <summary>Сбалансированная схема электропитания: overlay работает только поверх неё.</summary>
    internal static readonly Guid BalancedScheme = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    /// <summary>Почему не записали (или записали — <see cref="Skip.None"/>).</summary>
    internal enum Skip { None, Disabled, ServiceRunning, OtherScheme, NoMode, AlreadySet, Unavailable }

    private readonly AppConfig _cfg;
    private readonly IMifsClient _mifs;
    private readonly IPowerEvents _power;
    private readonly IPowerOverlay _overlay;
    private readonly Func<OemServiceState> _service;

    private readonly object _lock = new();
    // что записали мы, по источнику (true — сеть): выключая опцию, откатываем только своё,
    // выбор человека в параметрах Windows не трогаем
    private readonly Dictionary<bool, Guid> _written = [];
    private Skip _lastSkip = Skip.None;

    public PowerModeSync(AppConfig cfg, IMifsClient mifs, IPowerEvents power)
        : this(cfg, mifs, power, NativeOverlay.Instance,
               () => OemService.Read(OemService.DeviceService).State) { }

    internal PowerModeSync(AppConfig cfg, IMifsClient mifs, IPowerEvents power,
        IPowerOverlay overlay, Func<OemServiceState> service)
    {
        _cfg = cfg;
        _mifs = mifs;
        _power = power;
        _overlay = overlay;
        _service = service;
        _power.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>Режим ноутбука → режим питания Windows. Таблица фиксированная (вариант А).</summary>
    public static Guid? OverlayFor(PerfMode mode) => mode switch
    {
        PerfMode.Eco or PerfMode.Quiet => Efficiency,
        PerfMode.Auto or PerfMode.Balance => Balanced,
        PerfMode.Turbo or PerfMode.FullSpeed => Performance,
        _ => null,
    };

    /// <summary>
    /// Писать ли и что. Схема неизвестна (<paramref name="scheme"/> null) — пробуем: не смогли
    /// прочитать не значит «не та». Текущий overlay неизвестен — тоже пишем, запись идемпотентна.
    /// </summary>
    internal static Skip Decide(bool enabled, OemServiceState service, Guid? scheme,
        PerfMode? mode, Guid? actual, out Guid target)
    {
        target = Balanced;
        if (!enabled) return Skip.Disabled;
        if (service == OemServiceState.Running) return Skip.ServiceRunning;
        if (scheme is Guid s && s != BalancedScheme) return Skip.OtherScheme;
        if (mode is not PerfMode m || OverlayFor(m) is not Guid t) return Skip.NoMode;
        target = t;
        return actual == t ? Skip.AlreadySet : Skip.None;
    }

    /// <summary>Откатывать ли overlay источника при выключении: только если там всё ещё наше и не «сбалансированный».</summary>
    internal static bool ShouldUndo(Guid? written, Guid? actual) =>
        written is Guid w && w != Balanced && actual == w;

    /// <summary>Пока работает служба Xiaomi, опция недоступна (тумблер неактивен).</summary>
    public bool BlockedByService => _service() == OemServiceState.Running;

    /// <summary>Сверить сейчас (на пуле). Звать после любой смены режима и при включении опции.</summary>
    public void Request()
    {
        if (!_cfg.PowerModeSync) return;
        Task.Run(Sync);
    }

    /// <summary>Опцию выключили: вернуть «сбалансированный» там, где стоит записанное нами.
    /// Другой источник этим API не достать — его откатим на ближайшей смене питания.</summary>
    public void Undo() => Task.Run(UndoCurrent);

    internal void Sync()
    {
        lock (_lock)
        {
            try
            {
                bool online = _power.IsOnline;
                var skip = Decide(_cfg.PowerModeSync, _service(), Scheme(), Mode(), Actual(), out var target);
                if (skip == Skip.None)
                {
                    if (_overlay.SetActive(target))
                    {
                        _written[online] = target;
                        Log.Write($"PowerModeSync: {(online ? "сеть" : "батарея")} → {Name(target)}");
                    }
                    else skip = Skip.Unavailable;
                }
                // причину пропуска пишем один раз, а не на каждое 0x16: служба может работать неделями
                if (skip != _lastSkip && skip is not (Skip.None or Skip.AlreadySet or Skip.Disabled))
                    Log.Write($"PowerModeSync: пропуск — {skip}");
                _lastSkip = skip;
            }
            catch (Exception ex) { Log.Ex("PowerModeSync", ex); }
        }
    }

    internal void UndoCurrent()
    {
        lock (_lock)
        {
            try
            {
                bool online = _power.IsOnline;
                if (!_written.TryGetValue(online, out var w)) return;
                if (ShouldUndo(w, Actual()) && _overlay.SetActive(Balanced))
                    Log.Write($"PowerModeSync: выключено, {(online ? "сеть" : "батарея")} → сбалансированный");
                _written.Remove(online);
            }
            catch (Exception ex) { Log.Ex("PowerModeSync.Undo", ex); }
        }
    }

    private void OnPowerModeChanged(PowerModes mode)
    {
        if (mode is not (PowerModes.Resume or PowerModes.StatusChange)) return;
        // включено — сверить новый источник; выключено — доделать откат, если он остался на нём
        if (_cfg.PowerModeSync) Request();
        else if (_written.Count > 0) Undo();
    }

    private PerfMode? Mode()
    {
        try { return _mifs.GetPerfMode(); }
        catch (Exception ex) { Log.Ex("PowerModeSync.Mode", ex); return null; }
    }

    private Guid? Scheme() => _overlay.TryGetScheme(out var s) ? s : null;
    private Guid? Actual() => _overlay.TryGetOverlay(out var g) ? g : null;

    private static string Name(Guid g) =>
        g == Efficiency ? "эффективность" : g == Performance ? "производительность" : "сбалансированный";

    public void Dispose() => _power.PowerModeChanged -= OnPowerModeChanged;

    /// <summary>Шов над overlay режима питания — живой powrprof юнитами не трогаем.</summary>
    internal interface IPowerOverlay
    {
        bool TryGetScheme(out Guid scheme);
        bool TryGetOverlay(out Guid overlay);
        bool SetActive(Guid overlay);
    }

    /// <summary>
    /// powrprof.dll. <c>PowerGetActualOverlayScheme</c>/<c>PowerSetActiveOverlayScheme</c> не
    /// документированы (ими пользуются «Параметры» Windows и служба Xiaomi); на сборке, где их
    /// нет, вызов бросит EntryPointNotFoundException — считаем функцию недоступной и молчим.
    /// </summary>
    private sealed class NativeOverlay : IPowerOverlay
    {
        public static readonly NativeOverlay Instance = new();

        [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] private static extern uint PowerGetActualOverlayScheme(out Guid overlay);
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveOverlayScheme(ref Guid overlay);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);

        public bool TryGetScheme(out Guid scheme)
        {
            scheme = Guid.Empty;
            if (PowerGetActiveScheme(IntPtr.Zero, out var p) != 0) return false;
            try { scheme = Marshal.PtrToStructure<Guid>(p); return true; }
            finally { LocalFree(p); }
        }

        public bool TryGetOverlay(out Guid overlay)
        {
            overlay = Guid.Empty;
            try { return PowerGetActualOverlayScheme(out overlay) == 0; }
            catch (EntryPointNotFoundException) { return false; }
        }

        public bool SetActive(Guid overlay)
        {
            try
            {
                uint code = PowerSetActiveOverlayScheme(ref overlay);
                if (code != 0) Log.Write($"PowerModeSync: set=0x{code:X}");
                return code == 0;
            }
            catch (EntryPointNotFoundException)
            {
                Log.Write("PowerModeSync: PowerSetActiveOverlayScheme нет в этой Windows");
                return false;
            }
        }
    }
}
