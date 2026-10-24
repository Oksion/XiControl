using FluentAssertions;
using XiControl.SystemIntegration;
using Xunit;
using static XiControl.SystemIntegration.PanelFirmwareProtocol;

namespace XiControl.Tests;

/// <summary>
/// Версия прошивки контроллера панели (XIC-104). Эталон — последовательность из
/// <c>IGCLCSharpDLL.dll</c> OEM (docs/16): записи в контроллер должны совпадать с ней байт в байт,
/// ничего сверх. Сам IGCL — глазами, за ноутбуком (по RDP панели не видно).
/// </summary>
public sealed class PanelFirmwareProtocolTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    [Fact]
    public void Чтение_регистра_повторяет_OEM_байт_в_байт()
    {
        var ops = RegisterRead(RegRomVersion);

        ops.Select(o => (o.Op, o.Flags, o.Address, o.Size)).Should().Equal(
            (OpWrite, FlagI2c, 0xF2u, 2),
            (OpWrite, FlagI2c, 0xF2u, 2),
            (OpWrite, FlagI2cMot, 0xF2u, 10),
            (OpRead, FlagI2c, 0xF2u, 4));
        ops[0].Data.Should().Equal(Hex("0D 00"));
        ops[1].Data.Should().Equal(Hex("20 01"));
        ops[2].Data.Should().Equal(Hex("00 10 02 80 E7 00 00 00 00 00"), "адрес регистра — little-endian со второго байта");
        ops[3].Data.Should().BeEmpty();
    }

    [Fact]
    public void Опознание_только_по_OUI_Himax()
    {
        ReadOui().Should().Match<AuxOp>(o => o.Op == OpRead && o.Flags == FlagNative && o.Address == 0x400 && o.Size == 3);
        IsHimax(Hex("94 0B D5")).Should().BeTrue();
        IsHimax(Hex("00 1C F8")).Should().BeFalse();
        IsHimax(Hex("94 0B")).Should().BeFalse();
    }

    [Fact]
    public void Аргументы_по_раскладке_ctl_aux_access_args_t()
    {
        var a = Args(RegisterRead(RegRomVersion)[2]);

        a.Should().HaveCount(0xB0);
        a[0x00..0x04].Should().Equal(Hex("B0 00 00 00"));            // Size
        a[0x08..0x0C].Should().Equal(Hex("02 00 00 00"));            // OpType: запись
        a[0x0C..0x10].Should().Equal(Hex("04 00 00 00"));            // Flags: I2C MOT
        a[0x10..0x14].Should().Equal(Hex("F2 00 00 00"));            // Address
        a[0x24..0x28].Should().Equal(Hex("0A 00 00 00"));            // DataSize
        a[0x28..0x32].Should().Equal(Hex("00 10 02 80 E7 00 00 00 00 00"));
        a[0x32..].Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public void Ответ_берётся_по_DataSize()
    {
        var a = Args(new AuxOp(OpRead, FlagI2c, HimaxI2c, [], 4));
        Hex("06 0B 57 00").CopyTo(a, 0x28);

        Result(a).Should().Equal(Hex("06 0B 57 00"));
        a[0x24] = 0;
        Result(a).Should().BeEmpty();
        a[0x24] = 200; // больше буфера — мусор, а не данные
        Result(a).Should().BeEmpty();
    }

    [Theory]
    [InlineData("06 0B 57 00", "570B06")] // TM2424 на момент XIC-103
    [InlineData("08 0B 74 00", "740B08")] // в формате каталога
    [InlineData("00 00 00 00", null)]
    [InlineData("06 0B", null)]
    public void ROM_Ver_в_формате_каталога(string bytes, string? expected) =>
        RomVersion(Hex(bytes)).Should().Be(expected);
}
