using System.Reflection;

namespace XiControl.SystemIntegration;

/// <summary>
/// Звуки уведомлений. По умолчанию — встроенный WAV (EmbeddedResource `sound.&lt;имя&gt;.wav`),
/// но пользователь может указать свой файл. Проигрывание — в фоне (PlaySync на своём потоке).
/// </summary>
public static class Sound
{
    /// <summary>
    /// Джингл готовности «В дорогу» (батарея заряжена до 100%). Если задан <paramref name="customFile"/>
    /// и файл существует — играем его (только WAV/PCM), иначе — встроенный джингл.
    /// </summary>
    public static void PlayTravelReady(string? customFile = null) => Task.Run(() =>
    {
        try
        {
            var path = string.IsNullOrWhiteSpace(customFile)
                ? null
                : Environment.ExpandEnvironmentVariables(customFile.Trim());

            if (path is not null)
            {
                if (File.Exists(path)) { using var p = new System.Media.SoundPlayer(path); p.PlaySync(); return; }
                Log.Write($"Sound: свой WAV не найден ({path}) — играю встроенный");
            }

            PlayEmbedded("sound.travel-ready.wav");
        }
        catch (Exception ex) { Log.Ex("Sound.PlayTravelReady", ex); }
    });

    /// <summary>Сигнал переключения «В дорогу» под заблокированным экраном (XIC-11): вкл —
    /// восходящее арпеджио (три ноты), выкл — две нисходящие пониже и помедленнее; различаются
    /// направлением, числом нот и регистром. WAV-ы — в assets/sound (как travel-ready).</summary>
    public static void PlayToggle(bool on) => Task.Run(() =>
    {
        try { PlayEmbedded(on ? "sound.travel-on.wav" : "sound.travel-off.wav"); }
        catch (Exception ex) { Log.Ex("Sound.PlayToggle", ex); }
    });

    // Проиграть встроенный WAV (EmbeddedResource sound.<имя>.wav) синхронно — зовущие уже в фоне
    private static void PlayEmbedded(string resource)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        if (stream is null) { Log.Write($"Sound: встроенный ресурс не найден: {resource}"); return; }
        using var player = new System.Media.SoundPlayer(stream);
        player.PlaySync();
    }
}
