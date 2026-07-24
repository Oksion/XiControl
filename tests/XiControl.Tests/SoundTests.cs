using System.Reflection;
using FluentAssertions;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Звуки «В дорогу» вкл/выкл (XIC-11) — WAV-ы из assets/sound встроены в сборку
/// (EmbeddedResource sound.*.wav) и валидны. Само проигрывание юнитами не трогаем.
/// </summary>
public sealed class SoundTests
{
    private static byte[] Resource(string name)
    {
        using var s = typeof(Sound).Assembly.GetManifestResourceStream(name);
        s.Should().NotBeNull($"ресурс {name} должен быть встроен (assets/sound + wildcard в csproj)");
        using var ms = new MemoryStream();
        s!.CopyTo(ms);
        return ms.ToArray();
    }

    [Theory]
    [InlineData("sound.travel-on.wav")]
    [InlineData("sound.travel-off.wav")]
    [InlineData("sound.travel-ready.wav")]
    public void EmbeddedWav_IsValidRiff(string name)
    {
        var wav = Resource(name);

        System.Text.Encoding.ASCII.GetString(wav, 0, 4).Should().Be("RIFF");
        System.Text.Encoding.ASCII.GetString(wav, 8, 4).Should().Be("WAVE");
        BitConverter.ToInt32(wav, 4).Should().Be(wav.Length - 8, "длина RIFF-чанка = файл минус 8 байт");
    }

    [Fact]
    public void ToggleSounds_AreDifferent()
    {
        // вкл (восходящее арпеджио) и выкл (нисходящий двутон) — разные файлы, различимы на слух
        Resource("sound.travel-on.wav").Should().NotEqual(Resource("sound.travel-off.wav"));
    }
}
