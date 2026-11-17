using System.Runtime.InteropServices;

namespace XiControl.SystemIntegration;

/// <summary>
/// Громкость устройства вывода по умолчанию через Core Audio (XIC-97) — для жеста по краю
/// тачпада, когда включён свой индикатор уровня. Эмуляция клавиш громкости вызывала окно
/// Windows, а наш индикатор яркости рядом с ним давал две плашки разом; системное окно
/// программно не закрыть, поэтому жест пишет громкость напрямую и показывает её нашей плашкой.
///
/// Свои записи помечаются <see cref="Context"/>: уведомление с чужим контекстом — это клавиши
/// Fn или другое приложение, тогда Windows покажет своё окно, и нашу плашку надо убрать
/// (<see cref="ForeignChange"/>). Устройство по умолчанию переполучается на каждый новый жест —
/// наушники и BT подключают на ходу.
/// </summary>
public sealed class VolumeControl : IDisposable
{
    private const int CLSCTX_ALL = 0x17;
    private const int DataFlow_Render = 0; // eRender
    private const int Role_Multimedia = 1; // eMultimedia — то, что крутят клавиши громкости

    /// <summary>Метка наших записей в уведомлениях Core Audio.</summary>
    public static readonly Guid Context = new("5869436F-6E74-726C-5869-43566F6C756D");

    private static readonly IntPtr ContextPtr = AllocContext();

    private IAudioEndpointVolume? _vol;
    private readonly Callback _callback;
    private readonly object _lock = new();

    /// <summary>Громкость сменил не жест (клавиши, другое приложение). Поток Core Audio!</summary>
    public event Action? ForeignChange;

    public VolumeControl() => _callback = new Callback(this);

    /// <summary>Переполучить устройство по умолчанию — в начале жеста.</summary>
    public void Refresh()
    {
        lock (_lock)
        {
            Release();
            try
            {
                var enumr = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                if (enumr.GetDefaultAudioEndpoint(DataFlow_Render, Role_Multimedia, out var dev) == 0 && dev != null)
                {
                    var iid = typeof(IAudioEndpointVolume).GUID;
                    if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj) == 0 && obj is IAudioEndpointVolume v)
                    {
                        _vol = v;
                        _ = v.RegisterControlChangeNotify(_callback);
                    }
                    Marshal.ReleaseComObject(dev);
                }
                Marshal.ReleaseComObject(enumr);
            }
            catch (Exception ex) { Log.Ex("VolumeControl.Refresh", ex); }
        }
    }

    /// <summary>
    /// Сдвинуть громкость на <paramref name="delta"/> процентов. Как клавиши Windows, снимает
    /// mute. Возвращает новый уровень 0–100; null — устройства нет.
    /// </summary>
    public int? Step(int delta)
    {
        lock (_lock)
        {
            if (_vol is null) Refresh();
            if (_vol is null) return null;
            try
            {
                if (_vol.GetMasterVolumeLevelScalar(out float now) != 0) return null;
                int level = Math.Clamp((int)Math.Round(now * 100) + delta, 0, 100);
                _ = _vol.SetMasterVolumeLevelScalar(level / 100f, ContextPtr);
                if (_vol.GetMute(out bool muted) == 0 && muted) _ = _vol.SetMute(false, ContextPtr);
                return level;
            }
            catch (Exception ex) { Log.Ex("VolumeControl.Step", ex); Release(); return null; }
        }
    }

    // под замком
    private void Release()
    {
        if (_vol is null) return;
        try { _ = _vol.UnregisterControlChangeNotify(_callback); } catch { /* устройство уже ушло */ }
        Marshal.ReleaseComObject(_vol);
        _vol = null;
    }

    public void Dispose()
    {
        lock (_lock) Release();
    }

    private static IntPtr AllocContext()
    {
        // живёт до конца процесса: Core Audio держит указатель только на время вызова, но
        // выделять его на каждый шаг незачем
        IntPtr p = Marshal.AllocHGlobal(16);
        Marshal.StructureToPtr(Context, p, false);
        return p;
    }

    // Уведомление Core Audio: первым полем AUDIO_VOLUME_NOTIFICATION_DATA идёт контекст записи
    private sealed class Callback(VolumeControl owner) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr data)
        {
            try
            {
                if (data != IntPtr.Zero && Marshal.PtrToStructure<Guid>(data) != Context) owner.ForeignChange?.Invoke();
            }
            catch (Exception ex) { Log.Ex("VolumeControl.OnNotify", ex); }
            return 0;
        }
    }
}

[ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolumeCallback
{
    [PreserveSig] int OnNotify(IntPtr pNotify);
}
