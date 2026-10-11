using System.Runtime.InteropServices;
using XiControl.Config;

namespace XiControl.SystemIntegration;

/// <summary>
/// Режим «Не спать»: пока включён — система не засыпает (SetThreadExecutionState),
/// а закрытие крышки НА ПИТАНИИ ОТ СЕТИ не уводит в сон (действие крышки AC →
/// «ничего не делать» через Power Management API). На батарее крышка не трогается —
/// там штатный сон. Экран дополнительно удерживается негаснущим — кроме случая, когда в
/// config.json задан <see cref="AppConfig.OwlIgnoreDisplay"/> (дефолт false = прежнее
/// поведение); с ним сова держит бодрой только систему, для удалённого доступа.
///
/// Исходное действие крышки хранится в config.json вместе со схемой питания, в которую его
/// надо вернуть (XIC-82): схему могут сменить, пока сова включена, и тогда возвращать значение
/// в ставшую активной — значит и исходную оставить с «ничего не делать», и новой подменить
/// чужое. Запись восстановления сохраняется ДО правки плана: упадём посреди — на следующем
/// старте есть что и куда вернуть. Не удалось вернуть — запись не стираем, попробуем снова.
/// </summary>
public static class AwakeMode
{
    private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;
    internal const uint LidDoNothing = 0;

    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint esFlags);

    /// <summary>Включить. Вызывать с UI-потока (ES_CONTINUOUS привязан к потоку). Запись
    /// восстановления крышки сохраняет сам; остальной конфиг — вызывающий.</summary>
    public static bool Enable(AppConfig cfg) => Enable(cfg, PowerPlan.Instance, SetThreadExecutionState);

    /// <summary>Выключить: вернуть действие крышки и снять запрет сна/гашения экрана.</summary>
    public static void Disable(AppConfig cfg) => Disable(cfg, PowerPlan.Instance, SetThreadExecutionState);

    internal static bool Enable(AppConfig cfg, IPowerPlan plan, Func<uint, uint> setExecState)
    {
        try
        {
            if (!plan.TryGetActive(out Guid scheme)) return false;

            // Прошлая запись восстановления — от другой схемы (сменили, пока сова была вкл, а
            // вернуть не вышло): сначала вернуть ту, иначе её исходное значение потеряется —
            // слот под запись один. Не вернулось — не включаемся, чем терять, куда возвращать.
            if (cfg.AwakeSavedLidScheme is Guid prior && prior != scheme && !RestoreLid(cfg, plan))
                return false;

            // исходное действие крышки запоминаем один раз (если уже включены — не перетирать)
            if (cfg.AwakeSavedLidAc is null)
            {
                if (!plan.TryReadLidAc(scheme, out uint cur))
                {
                    Log.Write("AwakeMode: действие крышки не прочиталось — без записи восстановления не переопределяем");
                    return false;
                }
                cfg.AwakeSavedLidAc = (int)cur;
            }
            cfg.AwakeSavedLidScheme = scheme;
            cfg.Save(); // запись восстановления — до правки плана

            if (!plan.WriteLidAc(scheme, LidDoNothing) || !plan.SetActive(scheme))
            {
                Log.Write("AwakeMode: действие крышки не переопределилось — откатываю");
                RestoreLid(cfg, plan);
                return false;
            }

            // ES_SYSTEM_REQUIRED — всегда, это и есть суть режима; экран отпускаем только по опции
            uint flags = ES_CONTINUOUS | ES_SYSTEM_REQUIRED;
            if (!cfg.OwlIgnoreDisplay) flags |= ES_DISPLAY_REQUIRED;

            // возвращает прежние флаги; 0 — вызов не принят (сон/экран не удержим) — тогда и
            // крышку не держим: «сова» либо целиком, либо никак
            if (setExecState(flags) == 0)
            {
                Log.Write("AwakeMode: SetThreadExecutionState не принял флаги — откатываю крышку");
                RestoreLid(cfg, plan);
                return false;
            }
            return true;
        }
        catch (Exception ex) { Log.Ex("AwakeMode.Enable", ex); RestoreLid(cfg, plan); return false; }
    }

    internal static void Disable(AppConfig cfg, IPowerPlan plan, Func<uint, uint> setExecState)
    {
        try
        {
            if (setExecState(ES_CONTINUOUS) == 0)
                Log.Write("AwakeMode: SetThreadExecutionState(сброс) не принят");
            RestoreLid(cfg, plan);
        }
        catch (Exception ex) { Log.Ex("AwakeMode.Disable", ex); }
    }

    /// <summary>
    /// Вернуть действие крышки в ту схему, где его меняли. Применять (SetActive) нужно, только
    /// если она сейчас активна: значение неактивной схемы Windows прочтёт сама при переключении.
    /// Запись без схемы — от версии до XIC-82: возвращаем, как раньше, в активную.
    /// true — вернули (или нечего); false — запись осталась, попробуем на следующем старте.
    /// </summary>
    internal static bool RestoreLid(AppConfig cfg, IPowerPlan plan)
    {
        try
        {
            if (cfg.AwakeSavedLidAc is not int saved) return true;
            if (!plan.TryGetActive(out Guid active)) return false;
            Guid target = cfg.AwakeSavedLidScheme ?? active;
            if (!plan.WriteLidAc(target, (uint)saved) || (target == active && !plan.SetActive(target)))
            {
                Log.Write("AwakeMode: действие крышки не восстановилось — запись оставляю, повторю на старте");
                return false;
            }
            cfg.AwakeSavedLidAc = null;
            cfg.AwakeSavedLidScheme = null;
            cfg.Save();
            return true;
        }
        catch (Exception ex) { Log.Ex("AwakeMode.RestoreLid", ex); return false; }
    }

    /// <summary>Шов над Power Management API: только то, что нужно крышке. Ради тестов
    /// логики восстановления — живой план питания юнитами не трогаем.</summary>
    internal interface IPowerPlan
    {
        bool TryGetActive(out Guid scheme);
        bool TryReadLidAc(Guid scheme, out uint value);
        bool WriteLidAc(Guid scheme, uint value);
        bool SetActive(Guid scheme);
    }

    private sealed class PowerPlan : IPowerPlan
    {
        public static readonly PowerPlan Instance = new();

        private static Guid SubButtons = new("4f971e89-eebd-4455-a8de-9e59040e7347"); // GUID_SYSTEM_BUTTON_SUBGROUP
        private static Guid LidAction = new("5ca83367-6e45-459f-a27b-476b1d01c936");  // GUID_LIDCLOSE_ACTION

        [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);

        public bool TryGetActive(out Guid scheme)
        {
            scheme = Guid.Empty;
            if (PowerGetActiveScheme(IntPtr.Zero, out var p) != 0) return false;
            try { scheme = Marshal.PtrToStructure<Guid>(p); return true; }
            finally { LocalFree(p); }
        }

        public bool TryReadLidAc(Guid scheme, out uint value) =>
            PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref SubButtons, ref LidAction, out value) == 0;

        public bool WriteLidAc(Guid scheme, uint value) => Logged("write",
            PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref SubButtons, ref LidAction, value));

        public bool SetActive(Guid scheme) => Logged("set", PowerSetActiveScheme(IntPtr.Zero, ref scheme));

        private static bool Logged(string op, uint code)
        {
            if (code != 0) Log.Write($"AwakeMode: {op}=0x{code:X}");
            return code == 0;
        }
    }
}
