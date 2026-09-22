using XiControl.Config;

namespace XiControl.SystemIntegration;

/// <summary>
/// Сенсорный экран — HID-коллекция дигитайзера Touch Screen (Usage Page 0x0D, Usage 0x04).
/// Механика «выключить/включить через родительский I2C-HID-узел, с фолбэком и самопочинкой
/// после перезагрузки» общая с тачпадом — в <see cref="HidNodeToggle"/>. Здесь только
/// параметры: какой TLC искать и где в конфиге хранить найденный узел.
/// </summary>
public sealed class TouchscreenControl(AppConfig cfg) : HidNodeToggle
{
    protected override string CompatId => "HID_DEVICE_UP:000D_U:0004"; // Touch Screen TLC
    protected override string LogName => "Touchscreen";
    protected override string? DeviceId { get => cfg.TouchscreenDeviceId; set => cfg.TouchscreenDeviceId = value; }
    protected override bool PersistOff { get => cfg.TouchscreenPersistOff; set => cfg.TouchscreenPersistOff = value; }
    protected override bool KeepOff => cfg.TouchscreenKeepOff;
    protected override void SaveConfig() => cfg.Save();
}
