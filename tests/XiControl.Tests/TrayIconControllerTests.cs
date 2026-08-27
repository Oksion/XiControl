using FluentAssertions;
using XiControl.Ui;
using XiControl.Wmi;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// TrayIconController — политика «когда перерисовывать значок» (план 2.1):
/// кэш режима, force при смене темы, опрос. Рендер (Apply) и системная тема
/// (LightTaskbar) — фейковые колбэки.
/// </summary>
public sealed class TrayIconControllerTests
{
    private readonly FakeMifsClient _mifs = new();
    private readonly FakeTimer _timer = new();
    private readonly List<(PerfMode? mode, bool light)> _applied = [];
    private bool _light;
    private readonly TrayIconController _icon;

    public TrayIconControllerTests()
    {
        Log.Enabled = false; // деградация пишет в лог — не сорим в реальный файл
        _icon = new TrayIconController(_mifs, _timer)
        {
            Apply = (m, l) => _applied.Add((m, l)),
            LightTaskbar = () => _light,
        };
    }

    [Fact]
    public void Start_AppliesInitialIcon_AndBeginsPolling()
    {
        _mifs.Mode = PerfMode.Quiet;

        _icon.Start();

        _applied.Should().Equal((PerfMode.Quiet, false));
        _timer.Running.Should().BeTrue();
    }

    [Fact]
    public void SameMode_DoesNotReapply()
    {
        _mifs.Mode = PerfMode.Quiet;
        _icon.Start();

        _timer.Fire();       // опрос: режим не менялся
        _icon.Refresh();     // и явный запрос — тоже без изменений

        _applied.Should().HaveCount(1, "без изменений значок не трогаем");
    }

    [Fact]
    public void ModeChanged_Reapplies()
    {
        _mifs.Mode = PerfMode.Quiet;
        _icon.Start();

        _mifs.Mode = PerfMode.Turbo; // сон/EC сменили режим извне
        _timer.Fire();

        _applied.Should().Equal((PerfMode.Quiet, false), (PerfMode.Turbo, false));
    }

    [Fact]
    public void ThemeChanged_ForcesReapply_WithFreshTaskbarColor()
    {
        _mifs.Mode = PerfMode.Auto;
        _icon.Start();

        _light = true;       // Windows перешла на светлую тему
        _icon.ThemeChanged();

        _applied.Should().Equal((PerfMode.Auto, false), (PerfMode.Auto, true));
    }

    [Fact]
    public void Poll_Reapplies_WhenTaskbarThemeChanged_EvenIfWindowsEventWasMissed()
    {
        _mifs.Mode = PerfMode.Auto;
        _icon.Start();

        _light = true;
        _timer.Fire();

        _applied.Should().Equal((PerfMode.Auto, false), (PerfMode.Auto, true));
    }

    [Fact]
    public void HardwareUnavailable_DegradesToNeutralIcon()
    {
        _mifs.Mode = PerfMode.Turbo;
        _icon.Start();

        _mifs.ThrowOnGetPerfMode = true; // железо отвалилось между опросами
        _timer.Fire();

        _applied.Should().Equal((PerfMode.Turbo, false), (null, false));
    }

    [Fact]
    public void Polled_FiresEveryRefresh_EvenWithoutModeChange()
    {
        var polled = new List<PerfMode?>();
        _icon.Polled = polled.Add;
        _mifs.Mode = PerfMode.Quiet;
        _icon.Start();

        _timer.Fire(); // режим не менялся: Apply молчит, но тултип должен обновиться

        _applied.Should().HaveCount(1);
        polled.Should().Equal(PerfMode.Quiet, PerfMode.Quiet);
    }

    [Fact]
    public void FirstRefresh_WithNullMode_StillApplies()
    {
        _mifs.Mode = null; // прошивка не ответила уже на старте

        _icon.Start();

        _applied.Should().Equal(((PerfMode?)null, false)); // нейтральный значок всё равно ставим
    }
}
