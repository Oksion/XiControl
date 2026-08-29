namespace XiControl.Wmi;

/// <summary>Как прошивка этой машины отвечает на MiInterface (issue #37, docs/01).</summary>
public enum MifsDialect
{
    /// <summary>Ещё не определён — решит первый пробный GET.</summary>
    Unknown,

    /// <summary>TM2424 и родня: <c>OUT[1]</c> — статус (<c>0x80</c>/<c>0xE0</c>),
    /// значение в <c>OUT[4]</c> (режим) и <c>OUT[6]</c> (группа <c>0x10</c>).</summary>
    Classic,

    /// <summary>TM2113: байта статуса нет вовсе — в <c>OUT[1]</c> эхо команды,
    /// значение в <c>OUT[2]</c>.</summary>
    Echo,

    /// <summary>Ответ не опознан: раскладка неизвестна, фичи прошивки молча выключаются.</summary>
    Unsupported,
}

/// <summary>
/// Разбор ответа прошивки по диалекту (XIC-43).
///
/// Раньше успехом считался строго <c>OUT[1] == 0x80</c>. На Redmi Book Pro 15 2022 статуса нет,
/// и XiControl объявлял неудачной ЛЮБУЮ команду — включая выполненные: свип на той машине
/// показал, что железо слушается, а «не сработало» в интерфейсе было нашей ошибкой чтения.
///
/// Диалект вычисляется в рантайме одним GET и намеренно НЕ кладётся в профиль модели: так оно
/// работает и на машине, о которой мы никогда не слышали. Вся развилка, на которой можно
/// ошибиться, живёт здесь — чистой логикой над <c>byte[]</c>, без WMI и под тестами.
/// </summary>
public static class MifsReply
{
    /// <summary>Где лежит значение в классическом диалекте: режим производительности.</summary>
    public const int PerfOffset = 4;

    /// <summary>Где лежит значение в классическом диалекте: группа <c>0x10</c> (заряд, сенсоры).</summary>
    public const int ChargeOffset = 6;

    private const int EchoOffset = 2;   // в эхо-диалекте значение всегда здесь

    /// <summary>
    /// Определить диалект по ответу на пробный GET. <paramref name="cmd"/> — посланная команда:
    /// в эхо-диалекте она возвращается в <c>OUT[1]</c>, в классическом там статус.
    /// Спутать нельзя: в классическом <c>OUT[1]</c> бывает только <c>0x80</c>/<c>0xE0</c>,
    /// а команда туда не попадает никогда.
    /// </summary>
    public static MifsDialect Detect(byte[] outData, byte cmd)
    {
        if (outData is null || outData.Length <= EchoOffset) return MifsDialect.Unsupported;

        byte first = outData[1];

        // 0xE0 — тоже классика: это статус «команда не поддерживается», а не чужая раскладка.
        // Без этой ветки машина, у которой пробная команда не поддержана, целиком уехала бы в
        // Unsupported и осталась бы вообще без управления прошивкой.
        if (first is Mifs.StatusOk or Mifs.StatusUnsupported) return MifsDialect.Classic;

        return first == cmd ? MifsDialect.Echo : MifsDialect.Unsupported;
    }

    /// <summary>
    /// Услышала ли прошивка команду. В эхо-диалекте это именно «услышала», а не «выполнила»:
    /// там ответ повторяет посланный код независимо от результата, поэтому запись обязана
    /// проверяться чтением-назад (так и делает <c>SetPerfMode</c>).
    /// </summary>
    public static bool Ok(MifsDialect dialect, byte[] outData, byte cmd) => dialect switch
    {
        MifsDialect.Classic => outData is { Length: > 1 } && outData[1] == Mifs.StatusOk,
        MifsDialect.Echo => outData is { Length: > EchoOffset } && outData[1] == cmd,
        _ => false,
    };

    /// <summary>
    /// Значение из ответа. <paramref name="classicOffset"/> — где оно лежит в классическом
    /// диалекте (<see cref="PerfOffset"/> / <see cref="ChargeOffset"/>); в эхо-диалекте значение
    /// всегда в <c>OUT[2]</c>.
    /// </summary>
    public static byte Value(MifsDialect dialect, byte[] outData, int classicOffset) => dialect switch
    {
        MifsDialect.Classic => At(outData, classicOffset),
        MifsDialect.Echo => At(outData, EchoOffset),
        _ => 0,
    };

    /// <summary>
    /// Несёт ли ответ группы <c>0x10</c> (порог заряда, ватты адаптера, здоровье батареи)
    /// настоящие данные.
    ///
    /// В эхо-диалекте <c>OUT[2]</c> для этой группы — эхо АРГУМЕНТА, а не значение: на запрос
    /// порога заряда вернётся <c>0x02</c>, и прочитав его как данные, мы показали бы человеку
    /// выдуманный лимит. Измерено на TM2113: запись кодов 1, 5 и 8 не меняет ответ вообще —
    /// группы там просто нет. Честный прочерк лучше правдоподобной лжи.
    /// </summary>
    public static bool CarriesChargeData(MifsDialect dialect) => dialect == MifsDialect.Classic;

    private static byte At(byte[] b, int i) => b is not null && i < b.Length ? b[i] : (byte)0;
}
