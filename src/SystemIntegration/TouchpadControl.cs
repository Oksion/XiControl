using XiControl.Config;

namespace XiControl.SystemIntegration;

/// <summary>
/// Тачпад — HID-коллекция Precision Touchpad (Usage Page 0x0D, Usage 0x05). У PTP две
/// коллекции (мышь + панель), поэтому гасить надо их общий родитель, иначе курсор остался
/// бы жив через мышиную — это и делает <see cref="HidNodeToggle"/>. Здесь только параметры:
/// какой TLC искать и где в конфиге хранить найденный узел.
/// </summary>
public sealed class TouchpadControl(AppConfig cfg) : HidNodeToggle
{
    protected override string CompatId => "HID_DEVICE_UP:000D_U:0005"; // Precision Touchpad TLC
    protected override string LogName => "Touchpad";
    protected override string? DeviceId { get => cfg.TouchpadDeviceId; set => cfg.TouchpadDeviceId = value; }
    protected override bool PersistOff { get => cfg.TouchpadPersistOff; set => cfg.TouchpadPersistOff = value; }
    protected override bool KeepOff => cfg.TouchpadKeepOff;
    protected override void SaveConfig() => cfg.Save();
}
