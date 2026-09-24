using FluentAssertions;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Сильное нажатие на тачпад (XIC-78): порог и «один раз за касание». Давления — из замеров
/// на TM2424: касание 35–55, обычный клик 125–150, сильное нажатие 900–1200.
/// </summary>
public sealed class HeavyPressDetectorTests
{
    private readonly HeavyPressDetector _d = new();

    private static TouchContact[] Finger(int pressure, int id = 0) => [new TouchContact(id, 0.5, 0.5, pressure)];
    private static readonly TouchContact[] Lifted = [];

    [Theory]
    [InlineData(47)]    // касание
    [InlineData(148)]   // обычный клик
    [InlineData(500)]   // ровно порог — ещё не сильное
    public void Touch_and_normal_click_do_not_fire(int pressure) =>
        _d.Update(Finger(pressure)).Should().BeFalse();

    [Fact]
    public void Force_press_fires_once_per_touch()
    {
        // нарастание давления внутри одного касания, как в живом кадре
        _d.Update(Finger(40)).Should().BeFalse();
        _d.Update(Finger(140)).Should().BeFalse();
        _d.Update(Finger(1028)).Should().BeTrue();
        // удержание продавленным и дрожь у порога — повторов нет
        _d.Update(Finger(1217)).Should().BeFalse();
        _d.Update(Finger(480)).Should().BeFalse();
        _d.Update(Finger(900)).Should().BeFalse();
    }

    [Fact]
    public void Lifting_all_fingers_rearms()
    {
        _d.Update(Finger(1000)).Should().BeTrue();
        _d.Update(Lifted).Should().BeFalse();
        _d.Update(Finger(1000)).Should().BeTrue();
    }

    [Fact]
    public void Any_finger_can_force_press()
    {
        // второй палец продавил, первый просто лежит
        _d.Update([new TouchContact(0, 0.2, 0.5, 45), new TouchContact(1, 0.7, 0.5, 1100)]).Should().BeTrue();
    }

    [Fact]
    public void Palm_without_pressure_never_fires()
    {
        // ладонь (без Confidence) читатель отдаёт с давлением 0, как бы сильно она ни давила
        _d.Update(Finger(0)).Should().BeFalse();
    }
}
