using Microsoft.Win32;
using XiControl.Config;

namespace XiControl.SystemIntegration;

/// <summary>
/// Мёртвая зона у нижнего края тачпада — штатная «curtain zone» Windows Precision Touchpad
/// (`SuperCurtainBottom`, Win10 1903+). Driver-free: одна машинная настройка в реестре, права
/// администратора у нас есть.
///
/// Что она делает на самом деле (проверено вживую на TM2424, XIC-24): гасится ИНИЦИАЦИЯ
/// касания в полосе — палец, впервые опустившийся туда, не двигает курсор и не даёт тап.
/// Контакт, начатый выше и вошедший в зону движением, продолжает отслеживаться нормально,
/// а НАЖАТИЕ в зоне срабатывает (гипотеза «non-depressible панель → подавили контакт,
/// подавили и клик» не подтвердилась). UI обязан говорить это честно — не обещать
/// «низ не реагирует вообще».
///
/// Единицы — himetric (сотые доли мм), поэтому мм × 100. Пишем в глобальную ветку, а не в
/// HKR устройства: ключи устройства перетирает переустановка драйвера. Выключение —
/// УДАЛЕНИЕ значения, а не запись нуля, чтобы не оставлять за собой мусор.
///
/// Зона живёт внутри AAP (palm rejection): выставит пользователь в параметрах Windows
/// «максимальную чувствительность» (<c>AAPThreshold = 0</c>) — зона молча перестанет
/// работать, поэтому <see cref="AapDisabled"/> показывается в настройках.
/// </summary>
public sealed class TouchpadDeadZone(AppConfig cfg, TouchpadControl pad)
{
    // один и тот же путь в HKLM (наша настройка зоны) и в HKCU (пользовательский AAPThreshold)
    private const string RegKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\PrecisionTouchPad";
    private const string ValueName = "SuperCurtainBottom";

    /// <summary>Пресеты высоты для комбо (мм). Панель TM2424 — 90 мм по вертикали.</summary>
    public static readonly int[] PresetsMm = [8, 10, 12, 15, 20];

    /// <summary>Мм → himetric (сотые доли миллиметра), единицы PTP-настройки.</summary>
    public static int ToHimetric(int mm) => mm * 100;

    /// <summary>Привести высоту к разумному диапазону: config.json правится руками,
    /// а 200 мм «съели бы» всю панель и выглядели бы как сломанный тачпад.</summary>
    public static int NormalizeMm(int mm) => Math.Clamp(mm, 1, 40);

    /// <summary>Тачпад найден в системе — без него опция бессмысленна.</summary>
    public bool Available => pad.Available;

    /// <summary>
    /// AAP выключен пользователем в параметрах Windows («максимальная чувствительность») —
    /// значит и наша зона не работает. null — состояние не прочиталось.
    /// </summary>
    public static bool? AapDisabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegKey);
                return key?.GetValue("AAPThreshold") is int t ? t == 0 : null;
            }
            catch (Exception ex) { Log.Ex("TouchpadDeadZone.AapDisabled", ex); return null; }
        }
    }

    /// <summary>
    /// Применить состояние из конфига: включено — записать высоту, выключено — удалить значение.
    /// Затем перезапустить узел тачпада, чтобы драйвер перечитал настройку (иначе она ждала бы
    /// перезахода в сеанс). Зовётся ТОЛЬКО по явному переключению пользователем — на старте и
    /// при обычном сохранении конфига реестр не трогаем (CLAUDE.md). true — применено.
    /// </summary>
    public bool Apply()
    {
        if (!Write()) return false;
        // рестарт может не пройти (устройство занято) — настройка при этом уже записана и
        // подхватится при следующем перезаходе в сеанс, поэтому это не провал операции
        if (!pad.Restart()) Log.Write("TouchpadDeadZone: узел не перезапустился — применится после перезахода в сеанс");
        return true;
    }

    private bool Write()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(RegKey);
            if (key is null) { Log.Write($"TouchpadDeadZone: не открыть HKLM\\{RegKey}"); return false; }

            if (cfg.TouchpadDeadZone)
            {
                int mm = NormalizeMm(cfg.TouchpadDeadZoneMm);
                key.SetValue(ValueName, ToHimetric(mm), RegistryValueKind.DWord);
                Log.Write($"TouchpadDeadZone: {mm} мм ({ToHimetric(mm)} himetric)");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Write("TouchpadDeadZone: выключена (значение удалено)");
            }
            return true;
        }
        catch (Exception ex) { Log.Ex("TouchpadDeadZone.Write", ex); return false; }
    }
}
