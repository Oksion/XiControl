namespace XiControl.SystemIntegration;

/// <summary>Сила вибрации тачпада — три ступени Xiaomi PC Manager.</summary>
public enum HapticsVibration { Low, Medium, High }

/// <summary>Что прочитано из тачпада: сила вибрации (null — пара вне ступеней PC Manager),
/// порог нажатия и сила щелчка краевых ползунков — в «сырых» единицах прошивки.</summary>
public sealed record TouchpadHapticsState(HapticsVibration? Vibration, int Pressure, int Slide);

/// <summary>
/// Протокол вендорского канала тачпада BLTP7853 (XIC-77): чистая логика кадров, без HID.
/// Восстановлен из кода <c>SvrCModule.dll</c> Xiaomi PC Manager 5.8.0.74
/// (<c>sc::TouchSettingManager</c>) и проверен на TM2424 — версия прошивки из ответа совпала
/// с логом PC Manager. micontrol публиковал этот формат с ошибками (сумма, порядок кадров,
/// уровень High) — сверяться с ним нельзя.
///
/// Кадр — output report коллекции COL05 (UsagePage 0xFF01), 33 байта:
/// <c>[0]=0x0D (Report ID) [1]=len+7 [2]=сумма [3]=фаза [5]=len+1 [8]=команда [9..]=данные</c>,
/// сумма — XOR байтов [3..31] плюс 1. Запись идёт парой кадров: сама команда, затем
/// подтверждение (команда 0x01 с данными <c>[cmd, 0x100-cmd]</c>). Ответ на запрос чтения —
/// input report того же ID, данные с байта 4. Всё хранится в самом тачпаде и переживает
/// сон и перезагрузку, поэтому гард не нужен.
/// </summary>
public static class TouchpadHapticsProtocol
{
    public const int FrameLength = 33;
    public const byte ReportId = 0x0D;
    public const byte CmdVibration = 0x5D;   // 2 short: пара «передач» мотора
    public const byte CmdPressure = 0x5B;    // 4 short: порог, порог×0.33, 500, 400
    public const byte CmdSlide = 0x58;       // 1 short: сила щелчка PulseFrame (XIC-73)
    public const byte CmdHeavyPress = 0x59;  // 1 short: второй щелчок прошивки на сильном нажатии (XIC-78)

    /// <summary>Значения 0x59 — ровно как шлёт PC Manager (<c>WriteEnableHeavyPress</c>):
    /// 1 — прошивка щёлкает второй раз, когда нажатие продавлено, 2 — не щёлкает (заводское).
    /// Само распознавание от флага не зависит: давление приходит в обычном вводе всегда.</summary>
    public const ushort HeavyPressOn = 1, HeavyPressOff = 2;

    /// <summary>Сила щелчка краевых ползунков: PC Manager ставит 80. Шкала та же, что у мотора
    /// кликов (56…128), но щелчок на каждом шаге и 128 на живом пальце — перебор, а 40 —
    /// в самый раз; поэтому ступени идут вниз от заводской, а не вверх.</summary>
    public static readonly int[] SlidePresets = [40, 60, 80];
    private const byte CmdCommit = 0x01;
    private const byte CmdPulse = 0x02;      // один щелчок мотора, силу задаёт 0x58
    private const byte PulseEffect = 0x32;   // единственный эффект, который шлёт PC Manager

    /// <summary>Порог, выставленный на заводе (TM2424). У PC Manager ступени 100/120/140, и в
    /// заводское после первого выбора там уже не вернуться.</summary>
    public const int FactoryPressure = 125;

    /// <summary>Ступени порога по нарастанию усилия. Средняя — заводская, а не 120 из PC Manager:
    /// разница 120/125 пальцем не ощущается, зато заводское остаётся достижимым и три ступени
    /// выглядят так же, как у вибрации. Чужое 120 вкладка покажет как «своё».</summary>
    public static readonly int[] PressurePresets = [100, FactoryPressure, 140];

    /// <summary>Пары «передач» мотора по ступеням — ровно как в WriteMotorGears.</summary>
    public static ushort[] VibrationValues(HapticsVibration level) => level switch
    {
        HapticsVibration.Low => [56, 80],
        HapticsVibration.Medium => [80, 104],
        _ => [104, 128],
    };

    /// <summary>Ступень по прочитанной паре; null — чужое значение (не из PC Manager).</summary>
    public static HapticsVibration? MatchVibration(ushort a, ushort b)
    {
        foreach (var level in Enum.GetValues<HapticsVibration>())
        {
            var v = VibrationValues(level);
            if (v[0] == a && v[1] == b) return level;
        }
        return null;
    }

    /// <summary>Данные записи порога. Второе значение прошивка всё равно пересчитывает сама
    /// как ⌊порог × 0.33⌋ (проверено: записанное 125/42 читается как 125/41), шлём так же.</summary>
    public static ushort[] PressureValues(int threshold) =>
        [(ushort)threshold, (ushort)(threshold * 33 / 100), 500, 400];

    /// <summary>Кадр записи команды (первый из пары).</summary>
    public static byte[] WriteFrame(byte cmd, IReadOnlyList<ushort> values)
    {
        var data = new byte[values.Count * 2];
        for (int i = 0; i < values.Count; i++)
        {
            data[i * 2] = (byte)values[i];
            data[i * 2 + 1] = (byte)(values[i] >> 8);
        }
        return Frame(cmd, data, first: true);
    }

    /// <summary>Кадр подтверждения записи: без него прошивка команду не применяет.</summary>
    public static byte[] CommitFrame(byte cmd) => Frame(CmdCommit, [cmd, (byte)(0x100 - cmd)], first: false);

    /// <summary>Один щелчок мотора (XIC-73). Снят с <c>PluginGesture.dll</c> PC Manager — плагина
    /// краевых жестов, который шлёт его на каждом шаге яркости/громкости: команда 0x02 с данными
    /// <c>[0x32, 0xCE]</c>, один кадр без подтверждения. Сила — отдельной командой 0x58
    /// («передача скольжения», PC Manager держит 80; у TM2424 так и стоит).</summary>
    public static byte[] PulseFrame() => Frame(CmdPulse, [PulseEffect, (byte)(0x100 - PulseEffect)], first: false);

    /// <summary>Запрос чтения: <paramref name="count"/> short в ответ.</summary>
    public static byte[] ReadRequest(byte cmd, int count)
    {
        var f = new byte[FrameLength];
        f[0] = ReportId;
        f[1] = 7;
        f[3] = 1;
        f[5] = 1;
        f[7] = (byte)(count * 2);
        f[8] = cmd;
        f[2] = Checksum(f);
        return f;
    }

    /// <summary>
    /// Разбор ответа: <paramref name="count"/> short с байта 4. null — ответ негодный: чужой ID,
    /// битая сумма или одни нули. Последнее — не ошибка, а «ещё не готов»: так тачпад
    /// иногда отвечает на первый запрос после простоя, и чтение надо повторить. Настоящие
    /// значения нулевыми не бывают.
    /// </summary>
    public static ushort[]? ParseResponse(byte[] r, int count)
    {
        if (r.Length < 4 + count * 2 || r[0] != ReportId || r[2] != Checksum(r)) return null;
        var values = new ushort[count];
        bool any = false;
        for (int i = 0; i < count; i++)
        {
            values[i] = (ushort)(r[4 + i * 2] | r[5 + i * 2] << 8);
            any |= values[i] != 0;
        }
        return any ? values : null;
    }

    /// <summary>XOR байтов [3..31] плюс 1 — одна формула для кадров и ответов.</summary>
    public static byte Checksum(byte[] f)
    {
        byte x = 0;
        for (int i = 3; i < Math.Min(f.Length, FrameLength - 1); i++) x ^= f[i];
        return (byte)(x + 1);
    }

    private static byte[] Frame(byte cmd, byte[] data, bool first)
    {
        var f = new byte[FrameLength];
        f[0] = ReportId;
        f[1] = (byte)(data.Length + 7);
        f[3] = first ? (byte)0 : (byte)1;
        f[5] = (byte)(data.Length + 1);
        f[8] = cmd;
        data.CopyTo(f, 9);
        f[2] = Checksum(f);
        return f;
    }
}
