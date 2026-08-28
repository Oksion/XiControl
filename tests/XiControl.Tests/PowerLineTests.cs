using FluentAssertions;
using XiControl.SystemIntegration;
using Xunit;
using NativePowerLineStatus = XiControl.SystemIntegration.PowerLineStatus;

namespace XiControl.Tests;

/// <summary>Трактовка ACLineStatus: батарея — только явный Offline, Unknown к ней не приравниваем
/// (иначе троттлинг включается на воткнутом зарядном). Перегрузка без аргумента читает живой
/// GetSystemPowerStatus и юнитом не покрывается.</summary>
public sealed class PowerLineTests
{
    [Theory]
    [InlineData(NativePowerLineStatus.Online, true)]
    [InlineData(NativePowerLineStatus.Unknown, true)]   // 255 бывает сразу после resume — не повод троттлить
    [InlineData(NativePowerLineStatus.Offline, false)]
    public void IsOnline_TreatsOnlyOfflineAsBattery(NativePowerLineStatus status, bool expected) =>
        PowerLine.IsOnline(status).Should().Be(expected);

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(0.504f, 50)]
    [InlineData(1f, 100)]
    public void BatteryPercent_RoundsValidWin32Fraction(float fraction, int expected) =>
        new PowerSnapshot(NativePowerLineStatus.Online, fraction).BatteryPercent.Should().Be(expected);

    [Theory]
    [InlineData(-1f)]
    [InlineData(1.01f)]
    [InlineData(2.55f)]
    public void BatteryPercent_RejectsUnknownOrInvalidFraction(float fraction) =>
        new PowerSnapshot(NativePowerLineStatus.Unknown, fraction).BatteryPercent.Should().BeNull();
}
