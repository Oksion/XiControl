using System.Runtime.InteropServices;

namespace XiControl.SystemIntegration;

/// <summary>Значения совпадают с Win32 SYSTEM_POWER_STATUS.ACLineStatus.</summary>
public enum PowerLineStatus : byte
{
    Offline = 0,
    Online = 1,
    Unknown = 255,
}

/// <summary>Снимок состояния питания из Win32 GetSystemPowerStatus.</summary>
public readonly record struct PowerSnapshot(PowerLineStatus LineStatus, float BatteryLifePercent)
{
    public int? BatteryPercent => BatteryLifePercent is >= 0f and <= 1f
        ? (int)Math.Round(BatteryLifePercent * 100)
        : null;
}

/// <summary>Тонкая Win32-обёртка без зависимости от WinForms.</summary>
public static class PowerStatus
{
    public static PowerSnapshot Read()
    {
        if (!GetSystemPowerStatus(out var status))
            return new PowerSnapshot(PowerLineStatus.Unknown, 2.55f);

        float battery = status.BatteryLifePercent == byte.MaxValue
            ? 2.55f
            : status.BatteryLifePercent / 100f;
        return new PowerSnapshot((PowerLineStatus)status.AcLineStatus, battery);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);
}
