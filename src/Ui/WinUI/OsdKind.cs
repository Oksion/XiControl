namespace XiControl.Ui;

public enum OsdKind
{
    Charging,
    ChargingLimited,
    OnBattery,
    Eco,
    Quiet,
    Auto,
    Turbo,
    Full,
    CareOn,
    CareOff,
    MicOn,
    MicOff,
    Backlight,
    BacklightMid,
    BacklightOff,
    BacklightAuto,
    FnLockOn,
    FnLockOff,
    CapsLockOn,
    CapsLockOff,
    RefreshRate,
    RefreshRateOff,
    Travel,
    TravelOff,
    TouchpadOn,
    TouchpadOff,
    TouchscreenOn,
    TouchscreenOff,
    Error,
}

public enum ChargeBadge { None, NoPd, Slow }
