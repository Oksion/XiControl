using FluentAssertions;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>Трактовка ACLineStatus: батарея — только явный Offline, Unknown к ней не приравниваем
/// (иначе троттлинг включается на воткнутом зарядном). Перегрузка без аргумента читает живой
/// GetSystemPowerStatus и юнитом не покрывается.</summary>
public sealed class PowerLineTests
{
    [Theory]
    [InlineData(PowerLineStatus.Online, true)]
    [InlineData(PowerLineStatus.Unknown, true)]   // 255 бывает сразу после resume — не повод троттлить
    [InlineData(PowerLineStatus.Offline, false)]
    public void IsOnline_TreatsOnlyOfflineAsBattery(PowerLineStatus status, bool expected) =>
        PowerLine.IsOnline(status).Should().Be(expected);
}
