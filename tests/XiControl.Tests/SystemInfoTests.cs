using FluentAssertions;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// SystemInfo — чистое форматирование сведений о железе (XIC-3). Сам WMI не трогаем:
/// на CI его нет, а значения машинно-зависимы.
/// </summary>
public sealed class SystemInfoTests
{
    [Fact]
    public void FormatModel_AppendsBoardCode()
    {
        SystemInfo.FormatModel("Xiaomi Book Pro 14", "TM2424")
            .Should().Be("Xiaomi Book Pro 14 (TM2424)");
    }

    [Fact]
    public void FormatModel_SkipsBoardAlreadyInName()
    {
        // иначе получалось бы «Redmi Book TM2424 (TM2424)»
        SystemInfo.FormatModel("Redmi Book TM2424", "tm2424")
            .Should().Be("Redmi Book TM2424");
    }

    [Theory]
    [InlineData(null, "TM2424", "TM2424")]   // модели нет — показываем хотя бы код платы
    [InlineData("Book Pro 14", null, "Book Pro 14")]
    [InlineData(null, null, null)]
    public void FormatModel_SurvivesMissingParts(string? model, string? board, string? expected) =>
        SystemInfo.FormatModel(model, board).Should().Be(expected);

    [Fact]
    public void FormatBios_JoinsVersionAndDate() =>
        SystemInfo.FormatBios("XMAPT4B0P0A0A", "2026-06-17").Should().Be("XMAPT4B0P0A0A · 2026-06-17");

    [Theory]
    [InlineData("XMAPT4B0P0A0A", null, "XMAPT4B0P0A0A")]
    [InlineData(null, "2026-06-17", null)]   // дата без версии сама по себе бессмысленна
    public void FormatBios_SurvivesMissingParts(string? bios, string? date, string? expected) =>
        SystemInfo.FormatBios(bios, date).Should().Be(expected);

    [Fact]
    public void Mask_KeepsLastFourOnly()
    {
        var masked = SystemInfo.Mask("77079/26S400093");

        masked.Should().EndWith("0093");
        masked.Should().NotContain("77079", "середина закрыта — скриншот вкладки уходит в публичный тред");
        masked.Should().StartWith("••••");
    }

    [Theory]
    [InlineData("ABCD", "••••")]   // короткий номер закрываем целиком
    [InlineData("AB", "••")]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    public void Mask_HandlesShortAndEmpty(string? serial, string? expected) =>
        SystemInfo.Mask(serial).Should().Be(expected);

    [Theory]
    [InlineData("To be filled by O.E.M.")]
    [InlineData("Default string")]
    [InlineData("System manufacturer")]
    [InlineData("None")]
    [InlineData("  ")]
    public void Clean_DropsOemPlaceholders(string raw) =>
        SystemInfo.Clean(raw).Should().BeNull("это заглушка вендора, а не значение");

    [Fact]
    public void Clean_TrimsRealValue() => SystemInfo.Clean("  TM2424 ").Should().Be("TM2424");

    [Fact]
    public void ParseCimDate_ConvertsWmiFormat() =>
        SystemInfo.ParseCimDate("20260617050000.000000+000").Should().Be("2026-06-17");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("не дата")]
    public void ParseCimDate_SurvivesGarbage(string? raw) => SystemInfo.ParseCimDate(raw).Should().BeNull();
}
