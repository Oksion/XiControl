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

    // ---- XIC-53: что делать с устройством на старте (issue #39) ----

    [Theory]
    [InlineData(false, null)]   // гасили не мы
    [InlineData(false, false)]  // выключено, но не нами — это Диспетчер устройств, не лезем
    [InlineData(false, true)]
    public void Гасили_не_мы_значит_не_трогаем(bool persistOff, bool? enabled) =>
        HidNodeToggle.DecideAfterBoot(persistOff, softOff: false, keepOff: false, enabled)
            .Should().Be(HidNodeToggle.BootAction.Nothing);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Просили_оставить_выключенным_значит_оставляем(bool? enabled) =>
        HidNodeToggle.DecideAfterBoot(persistOff: true, softOff: false, keepOff: true, enabled)
            .Should().Be(HidNodeToggle.BootAction.Nothing);

    [Fact]
    public void Гасили_мы_и_оно_выключено_включаем_обратно() =>
        HidNodeToggle.DecideAfterBoot(persistOff: true, softOff: false, keepOff: false, enabled: false)
            .Should().Be(HidNodeToggle.BootAction.Enable);

    [Fact]
    public void Устройство_не_нашлось_пробуем_включить()
    {
        // null — узел не найден (например, убран query-remove'ом): включение умеет его вернуть
        // пересканированием шины, поэтому пробуем, а не сдаёмся
        HidNodeToggle.DecideAfterBoot(persistOff: true, softOff: false, keepOff: false, enabled: null)
            .Should().Be(HidNodeToggle.BootAction.Enable);
    }

    [Fact]
    public void Уже_включено_снимаем_отметку_а_не_включаем_снова()
    {
        // Корень вечного цикла из issue #39: медленное, но успешное включение записывалось как
        // неудача, отметка не снималась НИКОГДА, и каждая загрузка снова включала экран —
        // чем бы человек его ни гасил в промежутке.
        HidNodeToggle.DecideAfterBoot(persistOff: true, softOff: false, keepOff: false, enabled: true)
            .Should().Be(HidNodeToggle.BootAction.ClearFlag);
    }

    // ---- XIC-79: мягкое отключение и «помнить выключенным» ----

    [Fact]
    public void Мягко_погасили_просили_помнить_и_оно_вернулось_гасим_снова()
    {
        // Сама ошибка: мягкое отключение после перезагрузки возвращается, а отметки не было —
        // и галочка «помнить выключенным» ни на что не влияла.
        HidNodeToggle.DecideAfterBoot(persistOff: false, softOff: true, keepOff: true, enabled: true)
            .Should().Be(HidNodeToggle.BootAction.Disable);
    }

    [Fact]
    public void Мягко_погасили_без_галочки_и_оно_вернулось_снимаем_отметку() =>
        // обещанное «выключение до перезагрузки» — перезагрузка была, отметка больше не нужна
        HidNodeToggle.DecideAfterBoot(persistOff: false, softOff: true, keepOff: false, enabled: true)
            .Should().Be(HidNodeToggle.BootAction.ClearFlag);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(null, false)]
    [InlineData(null, true)]
    public void Мягко_погасили_и_оно_всё_ещё_выключено_не_трогаем(bool? enabled, bool keepOff)
    {
        // Перезапустилось только приложение (обновление, падение): человек погасил устройство
        // сам и недавно — включать его на старте было бы регрессией против прежнего поведения.
        HidNodeToggle.DecideAfterBoot(persistOff: false, softOff: true, keepOff, enabled)
            .Should().Be(HidNodeToggle.BootAction.Nothing);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void Не_гасили_вовсе_галочка_ничего_не_делает(bool? enabled) =>
        // «помнить выключенным» без нашего отключения — не повод гасить устройство самим
        HidNodeToggle.DecideAfterBoot(persistOff: false, softOff: false, keepOff: true, enabled)
            .Should().Be(HidNodeToggle.BootAction.Nothing);
}
