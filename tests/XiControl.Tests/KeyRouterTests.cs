using FluentAssertions;
using XiControl.Config;
using XiControl.Input;
using XiControl.Wmi;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// KeyRouter — таблица «HID-код → действие» и «действие → команда» на фейках
/// (план 3.2/2.1). Жест Mi — настоящий, на фейковых таймерах: роутер должен
/// корректно кормить его Down/Up.
/// </summary>
public sealed class KeyRouterTests
{
    private readonly AppConfig _cfg = new();
    private readonly FakeTimer _hold = new();
    private readonly FakeTimer _click = new();
    private readonly List<string> _hits = [];
    private readonly KeyRouter _router;

    public KeyRouterTests()
    {
        Log.Enabled = false; // default-ветка пишет в лог — не сорим в реальный файл
        var mi = new MiButtonGesture(_hold, _click);
        _router = new KeyRouter(_cfg, mi)
        {
            CycleModes = () => _hits.Add("modes"),
            ToggleCharge = () => _hits.Add("charge"),
            TogglePanel = () => _hits.Add("panel"),
            ToggleOwl = () => _hits.Add("owl"),
            ToggleMonitor = () => _hits.Add("monitor"),
            ToggleTravel = () => _hits.Add("travel"),
            ToggleTouchpad = () => _hits.Add("touchpad"),
            ToggleTouchscreen = () => _hits.Add("touchscreen"),
            ToggleAutoBrightness = () => _hits.Add("autobright"),
            Projection = () => _hits.Add("projection"),
            OpenSettings = () => _hits.Add("settings"),
            Copilot = () => _hits.Add("copilot"),
            MediaPlayPause = () => _hits.Add("play"),
            MediaNext = () => _hits.Add("next"),
            MediaPrev = () => _hits.Add("prev"),
            MediaStop = () => _hits.Add("stop"),
            Calculator = () => _hits.Add("calc"),
            Launch = cmd => _hits.Add("launch:" + cmd),
            MicKey = v => _hits.Add("mic:" + v),
            BacklightKey = v => _hits.Add("backlight:" + v),
            FnLockKey = v => _hits.Add("fnlock:" + v),
        };
        mi.Click = () => _router.Run(_cfg.MiClickAction, _cfg.MiClickCommand);
    }

    [Theory]
    [InlineData("play")]
    [InlineData("next")]
    [InlineData("prev")]
    [InlineData("stop")]
    [InlineData("calc")]
    public void MediaAndCalcActions_RunTheirExecutor(string action)
    {
        _router.Run(action, null);

        _hits.Should().Equal(action);
    }

    [Fact]
    public void MiClick_RoutedThroughGesture_RunsConfiguredAction()
    {
        _cfg.MiClickAction = "modes";

        _router.Handle(Mifs.KeyMiDown, 0);
        _router.Handle(Mifs.KeyMiUp, 0);
        _click.Fire(); // окно двойного истекло

        _hits.Should().Equal("modes");
    }

    [Fact]
    public void ProjectionKey_OnlyValueZero_Fires()
    {
        _cfg.ProjKeyAction = "projection";

        _router.Handle(Mifs.KeyProjection, 2); // слабый зарядник — пропуск
        _hits.Should().BeEmpty();

        _router.Handle(Mifs.KeyProjection, 0);
        _hits.Should().Equal("projection");
    }

    [Fact]
    public void AiKey_RunsConfiguredAction()
    {
        _cfg.AiKeyAction = "copilot";

        _router.Handle(Mifs.KeyAiDown, 0);

        _hits.Should().Equal("copilot");
    }

    [Fact]
    public void SettingsKey_PanelOpen_AlwaysTogglesCharge()
    {
        _cfg.SettingsKeyAction = "settings"; // ремап — но при открытой панели игнорируется
        _router.PanelVisible = () => true;

        _router.Handle(Mifs.KeySettings, 0);

        _hits.Should().Equal("charge");
    }

    [Fact]
    public void SettingsKey_PanelClosed_RunsConfiguredAction()
    {
        _cfg.SettingsKeyAction = "settings";
        _router.PanelVisible = () => false;

        _router.Handle(Mifs.KeySettings, 0);

        _hits.Should().Equal("settings");
    }

    [Theory]
    [InlineData("mic:0", 0)]
    [InlineData("mic:1", 1)]
    public void MicKey_ForwardsValue(string expected, byte value)
    {
        _router.Handle(Mifs.KeyMic, value);

        _hits.Should().Equal(expected);
    }

    [Fact]
    public void NotificationKeys_ForwardValues()
    {
        _router.Handle(Mifs.KeyKbdBacklight, 0x80);
        _router.Handle(Mifs.KeyFnLock, 1);

        _hits.Should().Equal("backlight:128", "fnlock:1");
    }

    [Fact]
    public void UnknownCode_IsSilentlyLogged()
    {
        _router.Handle(0xEE, 0x42);

        _hits.Should().BeEmpty();
    }

    [Theory]
    [InlineData("modes", "modes")]
    [InlineData("charge", "charge")]
    [InlineData("panel", "panel")]
    [InlineData("monitor", "monitor")]
    [InlineData("travel", "travel")]
    public void Run_MapsSimpleActions(string action, string expected)
    {
        _router.Run(action, null);

        _hits.Should().Equal(expected);
    }

    [Theory]
    [InlineData("owl")]
    [InlineData("touchpad")]
    [InlineData("touchscreen")]
    public void Run_HiddenFeature_DoesNothing(string action)
    {
        _cfg.OwlMode = false;
        _cfg.TouchpadFeature = false;
        _cfg.TouchscreenFeature = false;

        _router.Run(action, null);

        _hits.Should().BeEmpty();
    }

    [Fact]
    public void Run_EnabledFeatures_Fire()
    {
        // OwlMode/TouchpadFeature/TouchscreenFeature включены по умолчанию
        _router.Run("owl", null);
        _router.Run("touchpad", null);
        _router.Run("touchscreen", null);

        _hits.Should().Equal("owl", "touchpad", "touchscreen");
    }

    // Гейт авто-яркости — не в конфиге, а в железе: без датчика фичи нет и в настройках.
    // Дефолт AutoBrightnessAvailable = false, поэтому несмонтированный роутер молчит сам.
    [Fact]
    public void Run_AutoBright_NoSensor_DoesNothing()
    {
        _router.Run("autobright", null);

        _hits.Should().BeEmpty();
    }

    [Fact]
    public void Run_AutoBright_WithSensor_Fires()
    {
        _router.AutoBrightnessAvailable = () => true;

        _router.Run("autobright", null);

        _hits.Should().Equal("autobright");
    }

    [Fact]
    public void Run_Launch_PassesCommand_AndSkipsEmpty()
    {
        _router.Run("launch", "  ");
        _router.Run("launch", null);
        _hits.Should().BeEmpty();

        _router.Run("launch", "notepad foo");
        _hits.Should().Equal("launch:notepad foo");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("none")]
    [InlineData("unknown-future-action")]
    public void Run_NoneOrUnknown_DoesNothing(string? action)
    {
        _router.Run(action, "cmd");

        _hits.Should().BeEmpty();
    }
}
