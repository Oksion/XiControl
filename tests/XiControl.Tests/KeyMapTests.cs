using FluentAssertions;
using XiControl.Config;
using XiControl.Input;
using XiControl.Wmi;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Карта кодов клавиш (XIC-38): дефолты TM2424 + переопределение из config.json для моделей,
/// где прошивка шлёт другие коды (TM2113 и прочие).
/// </summary>
public class KeyMapTests
{
    // ---- Разбор кода ----

    [Theory]
    [InlineData("0x18", 0x18)]
    [InlineData("0X19", 0x19)]
    [InlineData("0xff", 0xFF)]
    [InlineData(" 0x25 ", 0x25)]  // пробелы из ручной правки
    [InlineData("24", 24)]        // десятичное — тоже принимаем
    public void ParseCode_ПониматьХексИДесятичные(string raw, int expected) =>
        KeyMap.ParseCode(raw).Should().Be((byte)expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0x")]
    [InlineData("нет")]
    [InlineData("0x1FF")] // за пределами байта
    [InlineData("300")]
    public void ParseCode_МусорЭтоNull(string? raw) =>
        KeyMap.ParseCode(raw).Should().BeNull();

    // ---- Дефолты ----

    [Fact]
    public void Default_ЗаводскаяКартаTM2424()
    {
        var map = KeyMap.Default();

        map.Kind(Mifs.KeyMiDown).Should().Be(KeyKind.MiDown);
        map.Kind(Mifs.KeyMiUp).Should().Be(KeyKind.MiUp);
        map.Kind(Mifs.KeyFnLock).Should().Be(KeyKind.FnLock);
        map.Kind(Mifs.KeyCapsLock).Should().Be(KeyKind.CapsLock);
        map.Kind(Mifs.KeyPerformance).Should().Be(KeyKind.Performance);
        map.Kind(Mifs.KeyScreenshot).Should().Be(KeyKind.Screenshot);
        map.Kind(Mifs.KeyTaskView).Should().Be(KeyKind.TaskView);
        map.Kind(Mifs.KeyGameCenter).Should().Be(KeyKind.Reserved);
        map.Kind(Mifs.KeySupportAssistant).Should().Be(KeyKind.Reserved);
        map.Kind(Mifs.KeyOemDevice).Should().Be(KeyKind.Reserved);
        map.Kind(Mifs.KeyCalculator).Should().Be(KeyKind.Calculator);
        map.Kind(Mifs.KeyRefreshRateCompat).Should().Be(KeyKind.RefreshRate);
        map.Kind(Mifs.KeyRefreshRate).Should().Be(KeyKind.RefreshRate);
        map.Kind(Mifs.KeyCameraPrivacy).Should().Be(KeyKind.CameraPrivacy);
        map.Kind(0x08).Should().Be(KeyKind.Reserved);
        map.Kind(0x55).Should().Be(KeyKind.Unknown, "неизвестный код так и остаётся неизвестным");
    }

    [Fact]
    public void Default_ПодтверждённыеКодыДругихМоделей()
    {
        // TM2113: пара Mi-кнопки подтверждена отчётом пользователя (issue #37) — владельцу
        // такой модели править config.json уже не нужно
        var map = KeyMap.Default();

        map.Kind(0x18).Should().Be(KeyKind.MiDown);
        map.Kind(0x19).Should().Be(KeyKind.MiUp);
        map.Kind(Mifs.KeyMiDown).Should().Be(KeyKind.MiDown, "коды TM2424 при этом остаются");
    }

    [Fact]
    public void FromConfig_БезПереопределений_ЭтоДефолты()
    {
        var map = KeyMap.FromConfig(new AppConfig());

        map.Kind(Mifs.KeyMiDown).Should().Be(KeyKind.MiDown);
        map.Kind(0x18).Should().Be(KeyKind.MiDown);
    }

    // ---- Переопределение ----

    [Fact]
    public void FromConfig_ПереопределениеОживляетЧужуюМодель()
    {
        // TM2113: Mi-кнопка шлёт пару 0x18/0x19 вместо 0x25/0x26
        var cfg = new AppConfig { KeyCodes = new() { ["miDown"] = "0x18", ["miUp"] = "0x19" } };

        var map = KeyMap.FromConfig(cfg);

        map.Kind(0x18).Should().Be(KeyKind.MiDown);
        map.Kind(0x19).Should().Be(KeyKind.MiUp);
    }

    [Fact]
    public void FromConfig_СтарыйКодСлотаОсвобождается()
    {
        // иначе на чужой модели, где 0x25 — совсем другая клавиша, она дёргала бы Mi-жесты
        var cfg = new AppConfig { KeyCodes = new() { ["miDown"] = "0x18" } };

        KeyMap.FromConfig(cfg).Kind(Mifs.KeyMiDown).Should().Be(KeyKind.Unknown);
    }

    [Fact]
    public void FromConfig_ИмяСлотаБезРегистра()
    {
        var cfg = new AppConfig { KeyCodes = new() { ["MIDOWN"] = "0x18", ["fnlock"] = "0x07", ["CAPSLOCK"] = "0x09", ["PERFORMANCE"] = "0x16" } };

        var map = KeyMap.FromConfig(cfg);

        map.Kind(0x18).Should().Be(KeyKind.MiDown);
        map.Kind(0x07).Should().Be(KeyKind.FnLock);
        map.Kind(0x09).Should().Be(KeyKind.CapsLock);
        map.Kind(0x16).Should().Be(KeyKind.Performance);
    }

    [Theory]
    [InlineData("bogus", "0x18")]   // неизвестный слот
    [InlineData("miDown", "нет")]   // неразборчивый код
    public void FromConfig_МусорНеЛомаетКарту(string slot, string code)
    {
        var cfg = new AppConfig { KeyCodes = new() { [slot] = code } };

        var map = KeyMap.FromConfig(cfg);

        map.Kind(Mifs.KeyMiDown).Should().Be(KeyKind.MiDown, "кривая правка не отбирает рабочие клавиши");
        map.Kind(Mifs.KeyMiUp).Should().Be(KeyKind.MiUp);
    }

    [Fact]
    public void FromConfig_ПереездКодаМеждуСлотами()
    {
        // человек назначил Mi-кнопке код, который у нас числился за «настройками»
        var cfg = new AppConfig { KeyCodes = new() { ["miDown"] = "0x1B" } };

        var map = KeyMap.FromConfig(cfg);

        map.Kind(0x1B).Should().Be(KeyKind.MiDown, "новый хозяин кода важнее");
    }

    // ---- Роутер на переопределённой карте ----

    [Fact]
    public void KeyRouter_РаботаетПоПереопределённымКодам()
    {
        // жест Mi — настоящий, на фейковых таймерах (как в KeyRouterTests)
        Log.Enabled = false; // default-ветка пишет в лог — не сорим в реальный файл
        var cfg = new AppConfig { KeyCodes = new() { ["miDown"] = "0x18", ["miUp"] = "0x19" } };
        cfg.MigrateKeyActions();
        var (hold, click) = (new FakeTimer(), new FakeTimer());
        bool clicked = false;
        // колбэки жеста монтирует TrayApp; здесь проверяем ровно зону ответственности карты —
        // что переопределённые коды доходят до жеста Mi-кнопки
        var mi = new MiButtonGesture(hold, click) { Click = () => clicked = true };
        var router = new KeyRouter(cfg, mi);

        router.Handle(0x18, 1); // нажали Mi на TM2113
        router.Handle(0x19, 1); // отпустили
        click.Fire();           // окно ожидания второго клика истекло — это одиночный клик

        clicked.Should().BeTrue("код чужой модели должен кормить жест Mi-кнопки");
    }

    [Fact]
    public void KeyRouter_РодныеКодыПослеПереопределенияМолчат()
    {
        Log.Enabled = false;
        var cfg = new AppConfig { KeyCodes = new() { ["miDown"] = "0x18", ["miUp"] = "0x19" } };
        cfg.MigrateKeyActions();
        var (hold, click) = (new FakeTimer(), new FakeTimer());
        bool clicked = false;
        var mi = new MiButtonGesture(hold, click) { Click = () => clicked = true };
        var router = new KeyRouter(cfg, mi);

        router.Handle(Mifs.KeyMiDown, 1); // код TM2424 на перенастроенной модели
        router.Handle(Mifs.KeyMiUp, 1);
        click.Fire();

        clicked.Should().BeFalse("слот переехал на другой код — старый больше не Mi-кнопка");
    }
}
