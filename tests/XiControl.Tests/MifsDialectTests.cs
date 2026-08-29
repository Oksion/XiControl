using FluentAssertions;
using XiControl.Wmi;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Определение диалекта ответа прошивки (XIC-43). Байты в тестах — не выдуманные: это дампы
/// из отчётов владельцев, TM2424 снят своим пробником, TM2113 прислан в issue #37.
///
/// Цена ошибки здесь несимметрична. Ошибиться в сторону Echo на классической машине — сломать
/// единственное железо, на котором мы можем что-то проверить; поэтому классику проверяем и на
/// «ок», и на «не поддерживается», и на мусор.
/// </summary>
public sealed class MifsDialectTests
{
    // TM2424, GET 0x08: OUT[1]=0x80 статус, OUT[3]=0x08 эхо cmd, OUT[4]=0x02 режим (Quiet)
    private static readonly byte[] Classic = [0x00, 0x80, 0x00, 0x08, 0x02, 0x00, 0x00, 0x00];

    // TM2113, GET 0x08: OUT[1]=0x08 эхо cmd, OUT[2]=0x02 режим (Quiet), дальше нули
    private static readonly byte[] Echo = [0x00, 0x08, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00];

    [Fact]
    public void Статус_0x80_это_классика() =>
        MifsReply.Detect(Classic, Mifs.CmdPerf).Should().Be(MifsDialect.Classic);

    // 0xE0 — тоже статус, значит классика. Иначе машина, у которой пробная команда не
    // поддержана, целиком уехала бы в Unsupported и осталась без управления прошивкой.
    [Fact]
    public void Статус_0xE0_тоже_классика_а_не_чужая_раскладка() =>
        MifsReply.Detect([0x00, 0xE0, 0x00, 0x08, 0x00], Mifs.CmdPerf)
            .Should().Be(MifsDialect.Classic);

    [Fact]
    public void Эхо_команды_в_первом_байте_это_эхо_диалект() =>
        MifsReply.Detect(Echo, Mifs.CmdPerf).Should().Be(MifsDialect.Echo);

    [Theory]
    [InlineData(new byte[0])]                       // прошивка не ответила
    [InlineData(new byte[] { 0x00, 0x08 })]         // короче, чем место значения
    [InlineData(new byte[] { 0x00, 0x42, 0x00, 0x00 })] // ни статус, ни эхо
    public void Непонятный_ответ_выключает_фичи(byte[] outData) =>
        MifsReply.Detect(outData, Mifs.CmdPerf).Should().Be(MifsDialect.Unsupported);

    [Fact]
    public void Null_вместо_ответа_не_роняет_разбор() =>
        MifsReply.Detect(null!, Mifs.CmdPerf).Should().Be(MifsDialect.Unsupported);

    // Главное, ради чего задача: на TM2113 успехом считался только 0x80, и КАЖДАЯ команда
    // объявлялась неудачной — включая выполненные.
    [Fact]
    public void Эхо_диалект_больше_не_считает_каждую_команду_провалом()
    {
        MifsReply.Ok(MifsDialect.Echo, Echo, Mifs.CmdPerf).Should().BeTrue();
        MifsReply.Ok(MifsDialect.Classic, Echo, Mifs.CmdPerf).Should().BeFalse(
            "старое правило OUT[1]==0x80 на этом ответе и давало ложный отказ");
    }

    [Fact]
    public void Классика_различает_поддержано_и_нет()
    {
        MifsReply.Ok(MifsDialect.Classic, Classic, Mifs.CmdPerf).Should().BeTrue();
        MifsReply.Ok(MifsDialect.Classic, [0x00, 0xE0, 0x00], Mifs.CmdPerf).Should().BeFalse();
    }

    // Эхо — это «услышала», а не «выполнила»: ответ повторяет посланный код независимо от
    // результата. Ответ на ЧУЖУЮ команду успехом считаться не должен.
    [Fact]
    public void Эхо_чужой_команды_не_успех() =>
        MifsReply.Ok(MifsDialect.Echo, Echo, Mifs.CmdCharge).Should().BeFalse();

    [Theory]
    [InlineData(MifsDialect.Unknown)]
    [InlineData(MifsDialect.Unsupported)]
    public void Неопределённый_диалект_никогда_не_успех(MifsDialect dialect) =>
        MifsReply.Ok(dialect, Classic, Mifs.CmdPerf).Should().BeFalse();

    [Fact]
    public void Значение_режима_берётся_из_своего_места_в_каждом_диалекте()
    {
        MifsReply.Value(MifsDialect.Classic, Classic, MifsReply.PerfOffset).Should().Be(0x02);
        MifsReply.Value(MifsDialect.Echo, Echo, MifsReply.PerfOffset).Should().Be(0x02);
    }

    [Fact]
    public void Короткий_буфер_даёт_ноль_а_не_исключение() =>
        MifsReply.Value(MifsDialect.Classic, [0x00, 0x80], MifsReply.ChargeOffset).Should().Be(0);

    // Ловушка, в которую нельзя въехать: в эхо-диалекте OUT[2] для группы 0x10 — это эхо
    // АРГУМЕНТА. Прочитав его как данные, мы показали бы выдуманный порог заряда: на запрос
    // 0x10/0x02 машина вернёт 0x02, что по таблице кодов означало бы совсем другой процент.
    [Fact]
    public void В_эхо_диалекте_группа_заряда_данных_не_несёт()
    {
        MifsReply.CarriesChargeData(MifsDialect.Echo).Should().BeFalse();
        MifsReply.CarriesChargeData(MifsDialect.Classic).Should().BeTrue();
        MifsReply.CarriesChargeData(MifsDialect.Unknown).Should().BeFalse();
        MifsReply.CarriesChargeData(MifsDialect.Unsupported).Should().BeFalse();
    }

    // Насколько ловушка опасна — зависит от подкоманды, и это стоит зафиксировать явно.
    //
    // У порога заряда (0x10/0x02) эхо вернуло бы 0x02, а такого кода в таблице уровней нет —
    // получился бы null, то есть пронесло СЛУЧАЙНО, а не по устройству кода.
    //
    // А вот у сенсоров той же группы эхо — вполне правдоподобное число: на запрос ватт
    // адаптера (0x10/0x06) вернулось бы «6 Вт», на запрос здоровья батареи (0x10/0x01) —
    // «1%». Ни то ни другое не выглядит ошибкой, и человек поверил бы. Ради этого случая
    // CarriesChargeData и закрывает ВСЮ группу, а не отдельные подкоманды.
    [Fact]
    public void Эхо_группы_0x10_дало_бы_правдоподобную_чушь_в_сенсорах()
    {
        Mifs.ChargePercentForCode(Mifs.ChargeSubEnable).Should().BeNull(
            "с порогом заряда пронесло случайно: кода 0x02 в таблице уровней нет");

        // а здесь эхо неотличимо от настоящего показания
        Mifs.SensorAdapterWatts.Should().Be(6, "эхо прочиталось бы как «адаптер 6 Вт»");
        Mifs.SensorBatteryHealth.Should().Be(1, "эхо прочиталось бы как «износ до 1%»");

        MifsReply.CarriesChargeData(MifsDialect.Echo).Should().BeFalse(
            "поэтому группа закрыта целиком, а не по отдельным подкомандам");
    }
}
