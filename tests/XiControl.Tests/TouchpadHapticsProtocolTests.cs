using FluentAssertions;
using XiControl.SystemIntegration;
using Xunit;
using static XiControl.SystemIntegration.TouchpadHapticsProtocol;

namespace XiControl.Tests;

/// <summary>
/// Протокол вендорского канала тачпада (XIC-77). Эталоны — байты, снятые с живого TM2424
/// (кадры, которые тачпад принял, и его ответы), а не пересчёт по той же формуле: иначе тест
/// повторял бы код вместе с его ошибкой. Сам HID — глазами.
/// </summary>
public sealed class TouchpadHapticsProtocolTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    private static byte[] Padded(string s)
    {
        var f = new byte[FrameLength];
        Hex(s).CopyTo(f, 0);
        return f;
    }

    // ---- Кадры записи: то, что тачпад принял и подтвердил чтением ----

    [Theory]
    [InlineData(HapticsVibration.Medium, "0D 0B 61 00 00 05 00 00 5D 50 00 68 00")]
    [InlineData(HapticsVibration.Low, "0D 0B 31 00 00 05 00 00 5D 38 00 50 00")]
    public void Vibration_frame_matches_bytes_accepted_by_touchpad(HapticsVibration level, string expected) =>
        WriteFrame(CmdVibration, VibrationValues(level)).Should().Equal(Padded(expected));

    // заводские 125/42 прошивка всё равно хранит как 125/41 — шлём сразу так
    [Theory]
    [InlineData(100, "0D 0F 74 00 00 09 00 00 5B 64 00 21 00 F4 01 90 01")]
    [InlineData(125, "0D 0F 63 00 00 09 00 00 5B 7D 00 29 00 F4 01 90 01")]
    public void Pressure_frame_matches_bytes_accepted_by_touchpad(int threshold, string expected) =>
        WriteFrame(CmdPressure, PressureValues(threshold)).Should().Equal(Padded(expected));

    [Theory]
    [InlineData(CmdVibration, "0D 09 FE 01 00 03 00 00 01 5D A3")]
    [InlineData(CmdPressure, "0D 09 FE 01 00 03 00 00 01 5B A5")]
    public void Commit_frame_is_command_01_with_complement(byte cmd, string expected) =>
        CommitFrame(cmd).Should().Equal(Padded(expected));

    // щелчок из PluginGesture.dll PC Manager — этот кадр мотор TM2424 отработал (XIC-73)
    [Fact]
    public void Pulse_frame_matches_bytes_from_PluginGesture() =>
        PulseFrame().Should().Equal(Padded("0D 09 FD 01 00 03 00 00 02 32 CE"));

    // сила щелчка 0x58: запись 40 и подтверждение — ровно то, что тачпад принял и вернул 40;
    // подтверждение совпадает с кадром из PluginGesture.dll
    [Fact]
    public void Slide_strength_frames_match_live_bytes()
    {
        WriteFrame(CmdSlide, [40]).Should().Equal(Padded("0D 09 74 00 00 03 00 00 58 28 00"));
        CommitFrame(CmdSlide).Should().Equal(Padded("0D 09 F4 01 00 03 00 00 01 58 A8"));
        ReadRequest(CmdSlide, 1).Should().Equal(Padded("0D 07 5B 01 00 01 00 02 58"));
        ParseResponse(Padded("0D 04 29 00 28 00"), 1).Should().Equal(40);
        ParseResponse(Padded("0D 04 51 00 50 00"), 1).Should().Equal(80); // заводское
    }

    [Fact]
    public void Read_request_matches_ReadMotorGears() =>
        ReadRequest(CmdVibration, 2).Should().Equal(Padded("0D 07 5A 01 00 01 00 04 5D"));

    // ---- Ответы тачпада ----

    [Fact]
    public void Parses_live_responses()
    {
        ParseResponse(Padded("0D 06 39 00 50 00 68 00"), 2).Should().Equal(80, 104);
        ParseResponse(Padded("0D 0A 34 00 7D 00 2A 00 F4 01 90 01"), 4).Should().Equal(125, 42, 500, 400);
        // версия прошивки v44.25.11.0 — совпала с логом PC Manager
        ParseResponse(Padded("0D 06 3F 00 2C 19 0B 00"), 2).Should().Equal(0x192C, 0x000B);
    }

    [Fact]
    public void Empty_response_means_not_ready()
    {
        // так тачпад отвечает, если спросить слишком рано: сумма верна, данных нет
        ParseResponse(Padded("0D 06 01 00"), 2).Should().BeNull();
    }

    [Theory]
    [InlineData("0D 06 38 00 50 00 68 00")]   // битая сумма
    [InlineData("0E 06 39 00 50 00 68 00")]   // чужой Report ID
    public void Rejects_foreign_or_corrupt_response(string r) =>
        ParseResponse(Padded(r), 2).Should().BeNull();

    // ---- Уровни ----

    [Fact]
    public void Pressure_second_value_follows_firmware_rounding()
    {
        PressurePresets.Select(t => PressureValues(t)[1]).Should().Equal(33, 41, 46);
        PressureValues(120)[1].Should().Be(39); // ступень PC Manager, если выставлена извне
    }

    [Fact]
    public void Vibration_levels_round_trip_and_foreign_pair_is_unknown()
    {
        foreach (var level in Enum.GetValues<HapticsVibration>())
        {
            var v = VibrationValues(level);
            MatchVibration(v[0], v[1]).Should().Be(level);
        }
        VibrationValues(HapticsVibration.High).Should().Equal(104, 128); // не 104/104, как у micontrol
        MatchVibration(104, 104).Should().BeNull();
    }
}
