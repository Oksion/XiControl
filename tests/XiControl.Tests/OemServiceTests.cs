using FluentAssertions;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Служба Xiaomi MiDeviceService (XIC-87): что запоминать перед «Отключить», что возвращать по
/// «Включить» и как показывать состояние. Саму службу (SCM) юнитами не трогаем.
/// </summary>
public sealed class OemServiceTests
{
    // Коды SERVICE_STATUS.dwCurrentState
    private const uint Stopped = 1, StartPending = 2, StopPending = 3, Running = 4;

    [Theory]
    [InlineData("Auto", OemStartType.Auto)]
    [InlineData("DelayedAuto", OemStartType.DelayedAuto)]
    [InlineData("Manual", OemStartType.Manual)]
    [InlineData("manual", OemStartType.Manual)]
    public void Включить_возвращает_запомненный_тип_запуска(string saved, OemStartType expected) =>
        OemService.RestoreTarget(saved).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Disabled")]
    [InlineData("Unknown")]
    [InlineData("мусор")]
    public void Без_годной_записи_включается_как_у_предустановки(string? saved) =>
        OemService.RestoreTarget(saved).Should().Be(OemStartType.Auto);

    [Theory]
    [InlineData(OemStartType.Auto, true)]
    [InlineData(OemStartType.DelayedAuto, true)]
    [InlineData(OemStartType.Manual, true)]
    [InlineData(OemStartType.Disabled, false)] // повторное «Отключить» не стирает исходный тип
    [InlineData(OemStartType.Unknown, false)]
    public void Перед_отключением_запоминаем_только_рабочий_тип(OemStartType current, bool remember) =>
        OemService.ShouldRemember(current).Should().Be(remember);

    [Fact]
    public void Запомненное_проходит_круг_через_строку_конфига()
    {
        foreach (var t in new[] { OemStartType.Auto, OemStartType.DelayedAuto, OemStartType.Manual })
            OemService.RestoreTarget(t.ToString()).Should().Be(t);
    }

    [Theory]
    [InlineData(Stopped, OemStartType.Auto, OemServiceState.Stopped)]
    [InlineData(Stopped, OemStartType.Manual, OemServiceState.Stopped)]
    [InlineData(Stopped, OemStartType.Disabled, OemServiceState.Disabled)]
    [InlineData(Running, OemStartType.Auto, OemServiceState.Running)]
    [InlineData(Running, OemStartType.Disabled, OemServiceState.Running)] // отключили, но не остановилась
    [InlineData(StartPending, OemStartType.Auto, OemServiceState.Running)]
    [InlineData(StopPending, OemStartType.Disabled, OemServiceState.Running)]
    public void Состояние_для_показа(uint scm, OemStartType start, OemServiceState expected) =>
        OemService.StateOf(scm, start).Should().Be(expected);
}
