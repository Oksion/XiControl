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

            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("sound.travel-ready.wav");
            if (stream is null) { Log.Write("Sound: встроенный ресурс не найден: sound.travel-ready.wav"); return; }
            using var player = new System.Media.SoundPlayer(stream);
            player.PlaySync();
        }
        catch (Exception ex) { Log.Ex("Sound.PlayTravelReady", ex); }
    });

    /// <summary>Короткий сигнал переключения «В дорогу» под заблокированным экраном (XIC-11):
    /// вкл — восходящий двутон, выкл — нисходящий. Синтезируется на лету (16-бит PCM, 44,1 кГц) —
    /// различимо на слух без бинарных ресурсов; проигрывание в фоне, как у джингла готовности.</summary>
    public static void PlayToggle(bool on) => Task.Run(() =>
    {
        try
        {
            using var ms = new MemoryStream(ToggleWav(on));
            using var player = new System.Media.SoundPlayer(ms);
            player.PlaySync();
        }
        catch (Exception ex) { Log.Ex("Sound.PlayToggle", ex); }
    });

    // WAV в памяти: два тона по ~90 мс (вкл 660→880 Гц, выкл наоборот), синусная огибающая
    // на каждом тоне — без щелчков на стыках. internal — заголовок/длину проверяет юнит-тест.
    internal static byte[] ToggleWav(bool on)
    {
        const int rate = 44100, toneMs = 90;
        float[] freqs = on ? [660f, 880f] : [880f, 660f];
        int toneLen = rate * toneMs / 1000;
        short[] pcm = new short[toneLen * freqs.Length];
        for (int t = 0; t < freqs.Length; t++)
            for (int i = 0; i < toneLen; i++)
            {
                double env = Math.Sin(Math.PI * i / toneLen); // подъём-спад громкости внутри тона
                double s = Math.Sin(2 * Math.PI * freqs[t] * i / rate);
                pcm[t * toneLen + i] = (short)(s * env * 0.35 * short.MaxValue);
            }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataLen = pcm.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); // PCM, моно
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataLen);
        foreach (short s in pcm) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
