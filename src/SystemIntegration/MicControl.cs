using System.Runtime.InteropServices;

namespace XiControl.SystemIntegration;

/// <summary>
/// Управление mute микрофона по умолчанию через Core Audio (IAudioEndpointVolume).
/// Минимальный COM-интероп: нужны только SetMute/GetMute.
/// </summary>
public sealed class MicControl : IDisposable
{
    private const int CLSCTX_ALL = 0x17;
    private const int DataFlow_Capture = 1; // eCapture
    private const int Role_Console = 0;     // eConsole

    private IAudioEndpointVolume? _vol;

    public MicControl()
    {
        try
        {
            var enumr = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumr.GetDefaultAudioEndpoint(DataFlow_Capture, Role_Console, out var dev) == 0 && dev != null)
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj) == 0 && obj is IAudioEndpointVolume v)
                    _vol = v;
                Marshal.ReleaseComObject(dev);
            }
            Marshal.ReleaseComObject(enumr);
        }
        catch (Exception ex) { Log.Ex("MicControl", ex); /* нет устройства захвата — недоступно */ }
    }

    public bool Available => _vol != null;

    public void SetMute(bool mute)
    {
        try { _vol?.SetMute(mute, IntPtr.Zero); }
        catch (Exception ex) { Log.Ex("MicControl.SetMute", ex); } // мьют не применился — это стоит видеть в логе
    }

    public bool? GetMute()
    {
        if (_vol == null) return null;
        try { return _vol.GetMute(out bool m) == 0 ? m : null; }
        catch (Exception ex) { Log.Ex("MicControl.GetMute", ex); return null; }
    }

    public void Dispose()
    {
        if (_vol != null) { Marshal.ReleaseComObject(_vol); _vol = null; }
    }
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumerator { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr ppDevices); // слот 0 (не используем)
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice); // слот 1
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                 [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface); // слот 0
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    int RegisterControlChangeNotify(IAudioEndpointVolumeCallback client);   // слот 0 (громкость, XIC-97)
    int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback client); // слот 1
    int f2(); int f3();                                                     // слоты 2..3 (не используем)
    int SetMasterVolumeLevelScalar(float fLevel, IntPtr pguidEventContext); // слот 4
    int f5();
    int GetMasterVolumeLevelScalar(out float pfLevel);                      // слот 6
    int f7(); int f8(); int f9(); int f10();                                // слоты 7..10 (не используем)
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, IntPtr pguidEventContext); // слот 11
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);                       // слот 12
}
