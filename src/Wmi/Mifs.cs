namespace XiControl.Wmi;

/// <summary>
/// Константы протокола MIFS (Xiaomi/Redmi, ODM Bitland).
/// Расшифровано пробами на TM2424 — см. docs/01-wmi-protocol.md.
/// </summary>
public static class Mifs
{
    // WMI
    public const string Namespace = @"\\.\root\wmi";
    public const string ClassName = "MiCommonInterface";
    public const string MethodName = "MiInterface";
    public const string EventClass = "HID_EVENT20";
    public const int BufferSize = 32;

    // operation (offset 1)
    public const byte OpGet = 0xFA;
    public const byte OpSet = 0xFB;

    // статус ответа (OUT[1])
    public const byte StatusOk = 0x80;   // функция поддерживается
    // 0xE0 = не поддерживается

    // команды (offset 3)
    public const byte CmdPerf = 0x08;    // режим производительности
    public const byte CmdMic = 0x0A;     // микрофон
    public const byte CmdCharge = 0x10;  // защита заряда

    // под-функции команды 0x10 (offset 4). Помимо защиты заряда, эта группа отдаёт
    // сенсоры — подтверждено на TM2424 (см. reference/probe-sensors.ps1):
    public const byte ChargeSubEnable = 0x02;  // val = КОД уровня порога (не бинарно!) — см. таблицу ниже
    public const byte ChargeSubFlag = 0x03;    // индикатор зоны заряда
    public const byte SensorAdapterWatts = 0x06; // ватты подключённого PD-адаптера (0 = нет/не PD)
    public const byte SensorBatteryHealth = 0x01; // SOH1: здоровье батареи, % от исходной ёмкости

    // Mi-кнопка шлёт пару: 0x25 (нажатие) + 0x26 (отпускание).
    // Короткое нажатие = цикл режимов, долгое (удержание) = панель — логика в TrayApp.
    public const byte KeyMiDown = 0x25;
    public const byte KeyMiUp = 0x26;

    // Коды спец-клавиш HID_EVENT20 на TM2424 (см. docs/07-keymap.md)
    public const byte KeyMic = 0x21;         // микрофон: value 0=mute(лампа горит), 1=unmute
    public const byte KeyKbdBacklight = 0x05; // подсветка: value = уровень (0/5/10/0x80=Авто)
    public const byte KeyProjection = 0x01;   // проекция экрана (value 0)
    public const byte KeyScreenshot = 0x02;   // Ножницы / Win+Shift+S
    public const byte KeyTaskView = 0x03;     // представление задач / Win+Tab
    public const byte KeyOemDevice = 0x04;    // OEM Airplane/RadioControl; в XiControl известный no-op
    public const byte KeyTouchpadState = 0x06;// состояние тачпада: value 0/1
    public const byte KeySettings = 0x1B;     // клавиша «Настройки»
    public const byte KeyGameCenter = 0x0A;   // Xiaomi G Command Center; известный no-op
    public const byte KeyCalculator = 0x0B;   // калькулятор
    public const byte KeyLowPower = 0x0C;     // предупреждение слабого питания
    public const byte KeyNumLock = 0x0D;      // Num Lock: value 0/1
    public const byte KeyOemToggle = 0x0E;    // OEM toggle без независимого действия; известный no-op
    public const byte KeyTouchpadToggle = 0x10; // MIControl-совместимая карта: тачпад
    public const byte KeySupportAssistant = 0x12; // Xiaomi Support/Meeting Assistant; известный no-op
    public const byte KeyRefreshRateCompat = 0x13; // MIControl-совместимая карта герцовки
    public const byte KeyWinLock = 0x17;      // блокировка Windows-клавиши
    public const byte KeyRefreshRate = 0x1A;  // цикл частоты встроенной панели
    public const byte KeyAiDown = 0x23;       // нейропомощник (нажатие)
    public const byte KeyAiUp = 0x24;         // нейропомощник (отпускание)
    public const byte KeyFnLock = 0x07;       // Fn-Lock (Fn+Esc): value = новое состояние 0/1
    public const byte KeyCapsLock = 0x09;     // Caps Lock: value = новое состояние 0/1
    public const byte KeyPerformance = 0x16;  // смена режима: value = PerfMode; legacy 0 = Turbo
    public const byte KeyCameraPrivacy = 0xA0;// камера/приватность: value 0/1 (MIControl)

    /// <summary>Порог «беречь батарею» по умолчанию (не «фиксирован» — уровень выбирается, см. ниже).</summary>
    public const int ChargeThresholdPercent = 80;

    // ---- Уровни порога заряда (cmd 0x10 sub 0x02) ----
    // ПЕРЕОТКРЫТО 2026-07-29: sub 0x02 — не бинарный флаг, а КОД уровня. Прошивка держит
    // набор {0,1,4,5,6,7,8} и сама его валидирует (невалидные отвергает: SET → OUT[1]!=0x80,
    // и зажимает к ближайшему). granular-шкала: % = 120 − код·10 (коды 4–8). Разбор —
    // docs/12-charge-levels.md. Код 4 = 80% (granular-дубль) — наружу не пишем, используем legacy 1.

    /// <summary>Пресеты «беречь» для UI, по убыванию (без 100% — это «выкл защиты»).</summary>
    public static readonly int[] ChargeCarePresets = { 80, 70, 60, 50, 40 };

    /// <summary>Процент порога → код для записи в <see cref="ChargeSubEnable"/>; null — % не поддержан.</summary>
    public static byte? ChargeCodeForPercent(int percent) => percent switch
    {
        100 => 0, 80 => 1, 70 => 5, 60 => 6, 50 => 7, 40 => 8, _ => null,
    };

    /// <summary>Код из <see cref="ChargeSubEnable"/> → процент порога; null — код неизвестен.
    /// Код 4 трактуем как 80% (granular-дубль legacy 1).</summary>
    public static int? ChargePercentForCode(byte code) => code switch
    {
        0 => 100, 1 => 80, 4 => 80, 5 => 70, 6 => 60, 7 => 50, 8 => 40, _ => null,
    };
}

/// <summary>
/// Режимы производительности (cmd 0x08). На TM24 доступны все, кроме Balance.
/// </summary>
public enum PerfMode : byte
{
    Balance = 0x01,   // недоступен на TM24
    Quiet = 0x02,
    Turbo = 0x03,
    FullSpeed = 0x04, // требует питания от сети (на батарее прошивка не примет)
    Auto = 0x09,      // «умный», только TM24
    Eco = 0x0A,       // скрытый: значение из EC-референса (рег. 0x68), прошивка TM24 принимает
}
