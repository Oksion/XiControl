using FluentAssertions;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Sound.ToggleWav — синтез сигнала «В дорогу» вкл/выкл (XIC-11): корректный WAV-заголовок
/// и различимость направлений. Само проигрывание (SoundPlayer) юнитами не трогаем.
/// </summary>
public sealed class SoundTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ToggleWav_IsValidPcmWav(bool on)
    {
        var wav = Sound.ToggleWav(on);

        // RIFF/WAVE-заголовок и согласованная длина data-чанка
        System.Text.Encoding.ASCII.GetString(wav, 0, 4).Should().Be("RIFF");
        System.Text.Encoding.ASCII.GetString(wav, 8, 4).Should().Be("WAVE");
        int riffLen = BitConverter.ToInt32(wav, 4);
        riffLen.Should().Be(wav.Length - 8, "длина RIFF-чанка = файл минус 8 байт заголовка");
        int dataLen = BitConverter.ToInt32(wav, 40);
        dataLen.Should().Be(wav.Length - 44, "PCM-данные идут сразу за 44-байтовым заголовком");
        dataLen.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ToggleWav_OnAndOff_AreDifferent()
    {
        // восходящий и нисходящий двутон — разные сэмплы (пользователь различает на слух)
        Sound.ToggleWav(true).Should().NotEqual(Sound.ToggleWav(false));
    }
}
