using System.Drawing;
using FluentAssertions;
using XiControl.Ui;
using Xunit;

namespace XiControl.Tests;

/// <summary>Индикатор уровня яркости (XIC-97): сглаживание полоски и акцентный цвет Windows.</summary>
public sealed class LevelFlyoutTests
{
    [Fact]
    public void Ease_ТретьОстаткаЗаКадр() =>
        LevelFlyout.Ease(40f, 70).Should().Be(50f);

    [Theory]
    [InlineData(69.6f, 70)]
    [InlineData(70.4f, 70)]
    public void Ease_ПоследнийПолпроцент_Сразу(float shown, int target) =>
        LevelFlyout.Ease(shown, target).Should().Be(target);

    [Fact]
    public void Ease_Доезжает()
    {
        float v = 0;
        for (int i = 0; i < 30 && v != 100; i++) v = LevelFlyout.Ease(v, 100);
        v.Should().Be(100);
    }

    // восемь цветов RGBA от Light3 до Dark3; значения — палитра синего акцента по умолчанию
    private static readonly byte[] Palette =
    [
        0x99, 0xEB, 0xFF, 0, 0x4C, 0xC2, 0xFF, 0, 0x00, 0x91, 0xF8, 0, 0x00, 0x78, 0xD4, 0,
        0x00, 0x5A, 0x9E, 0, 0x00, 0x42, 0x75, 0, 0x00, 0x26, 0x42, 0, 0xF7, 0x63, 0x0C, 0,
    ];

    [Fact]
    public void Accent_ТёмнаяТема_СветлыйОттенок() =>
        WindowsAccent.FromPalette(Palette, dark: true).Should().Be(Color.FromArgb(0x4C, 0xC2, 0xFF));

    [Fact]
    public void Accent_СветлаяТема_ТёмныйОттенок() =>
        WindowsAccent.FromPalette(Palette, dark: false).Should().Be(Color.FromArgb(0x00, 0x5A, 0x9E));

    [Fact]
    public void Accent_БитаяПалитра_Null() =>
        WindowsAccent.FromPalette([1, 2, 3], dark: true).Should().BeNull();

    [Fact]
    public void Accent_DwmХранитAbgr() =>
        WindowsAccent.FromAbgr(unchecked((int)0xFFD47800)).Should().Be(Color.FromArgb(0x00, 0x78, 0xD4));

    [Theory]
    [InlineData(EdgeLevel.Brightness, 0, '\uE706')]
    [InlineData(EdgeLevel.Volume, 0, '\uE992')]
    [InlineData(EdgeLevel.Volume, 33, '\uE993')]
    [InlineData(EdgeLevel.Volume, 66, '\uE994')]
    [InlineData(EdgeLevel.Volume, 100, '\uE995')]
    public void Glyph_КакУWindows(EdgeLevel kind, int level, char glyph) =>
        LevelFlyout.Glyph(kind, level).Should().Be(glyph.ToString());

    // Fn+F5 посреди показа: своё эхо WMI не прячет плашку, чужая правка — прячет
    [Theory]
    [InlineData(50, 50, 5000, true)]   // наш уровень, пусть и поздно
    [InlineData(48, 50, 200, true)]    // жест ещё идёт — событие отстало от шага
    [InlineData(60, 50, 1000, false)]  // Fn+F5: другой уровень после паузы
    public void IsOwnEcho(int level, int ours, long sinceMs, bool echo) =>
        LevelFlyout.IsOwnEcho(level, ours, sinceMs).Should().Be(echo);
}
