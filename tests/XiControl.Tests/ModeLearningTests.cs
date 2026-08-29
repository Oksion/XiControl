using FluentAssertions;
using XiControl.Config;
using XiControl.Wmi;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Выучивание набора режимов по отказам прошивки (XIC-44). Главное здесь — не спрятать
/// лишнего: режим, отвергнутый только на батарее, на сети обычно работает.
/// </summary>
public sealed class ModeLearningTests
{
    [Fact]
    public void Отказ_записывается_по_источнику_питания()
    {
        var rejected = new Dictionary<string, List<PerfMode>>();

        ModeLearning.Record(rejected, PerfMode.Turbo, online: true).Should().BeTrue();

        rejected[ModeLearning.Ac].Should().Equal(PerfMode.Turbo);
        rejected.Should().NotContainKey(ModeLearning.Battery);
    }

    [Fact]
    public void Повторный_отказ_ничего_не_меняет()
    {
        var rejected = new Dictionary<string, List<PerfMode>>();
        ModeLearning.Record(rejected, PerfMode.Turbo, online: true);

        ModeLearning.Record(rejected, PerfMode.Turbo, online: true).Should().BeFalse(
            "нечего сохранять — состояние прежнее");
        rejected[ModeLearning.Ac].Should().Equal(PerfMode.Turbo);
    }

    // Ради чего вообще разрез по питанию: «Полная мощность» на TM2424 поддержана, но на
    // батарее прошивка её не примет. Спрятать её из-за этого было бы потерей рабочего режима.
    [Fact]
    public void Отказ_только_на_батарее_не_повод_прятать()
    {
        var rejected = new Dictionary<string, List<PerfMode>>();
        ModeLearning.Record(rejected, PerfMode.FullSpeed, online: false);

        ModeLearning.RejectedEverywhere(rejected, PerfMode.FullSpeed).Should().BeFalse();
    }

    [Fact]
    public void Отказ_на_обоих_источниках_значит_режима_нет()
    {
        var rejected = new Dictionary<string, List<PerfMode>>();
        ModeLearning.Record(rejected, PerfMode.Balance, online: true);
        ModeLearning.Record(rejected, PerfMode.Balance, online: false);

        ModeLearning.RejectedEverywhere(rejected, PerfMode.Balance).Should().BeTrue();
    }

    [Fact]
    public void Пустое_состояние_никого_не_хоронит() =>
        ModeLearning.RejectedEverywhere(null, PerfMode.Turbo).Should().BeFalse();

    // Обновление BIOS может добавить режимы: помнить старые отказы вечно значит навсегда
    // лишить человека того, что теперь работает.
    [Theory]
    [InlineData("1.0", "1.1", true)]
    [InlineData("1.0", "1.0", false)]
    public void Смена_версии_BIOS_сбрасывает_выученное(string learned, string now, bool expired) =>
        ModeLearning.Expired(learned, now).Should().Be(expired);

    [Fact]
    public void Регистр_версии_BIOS_не_считается_сменой() =>
        ModeLearning.Expired("XMACN", "xmacn").Should().BeFalse();

    // Версия не прочиталась — это не повод забывать выученное: иначе одна осечка WMI
    // обнуляла бы знание о железе.
    [Theory]
    [InlineData("1.0", null)]
    [InlineData("1.0", "")]
    [InlineData(null, "1.0")]
    public void Пустая_версия_не_сбрасывает(string? learned, string? now) =>
        ModeLearning.Expired(learned, now).Should().BeFalse();
}
