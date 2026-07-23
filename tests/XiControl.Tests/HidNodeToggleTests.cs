using FluentAssertions;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// HidNodeToggle.IsBusOrController — чистое решение «это шина/контроллер, гасить нельзя».
/// Живые PnP-пути (SetupAPI/CfgMgr32) юнитами не покрываем — только этот выбор узла.
/// Регрессия, ради которой фикс: на Meteor Lake (TM2424) HID-коллекция тачскрина висит
/// прямо под PCI-контроллером (Intel Serial IO I2C / Touch Host Controller), и слепое
/// отключение родителя валило шину вместо «сенсора».
/// </summary>
public sealed class HidNodeToggleTests
{
    [Theory]
    [InlineData(@"PCI\VEN_8086&DEV_E448&SUBSYS_24241D72&REV_01\3&11583659&0&80")] // тот самый THC/I2C
    [InlineData(@"PCI\VEN_8086&DEV_A0D9")]
    [InlineData(@"pci\ven_8086&dev_e448")] // регистр не важен
    public void BusAndControllerNodes_AreRejected(string id) =>
        HidNodeToggle.IsBusOrController(id).Should().BeTrue();

    [Theory]
    [InlineData(@"ACPI\BLTP7853\4&2C8959B&0")]                      // родитель тачпада — гасить безопасно
    [InlineData(@"HID\VEN_04F3&DEV_311C&Col01\7&1a2b3c4d&0&0000")]  // сама HID-коллекция (фолбэк-цель)
    [InlineData("")]
    public void HidAndAcpiNodes_AreAllowed(string id) =>
        HidNodeToggle.IsBusOrController(id).Should().BeFalse();
}
