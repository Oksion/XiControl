using FluentAssertions;
using XiControl.Config;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// AwakeMode (XIC-82): возврат действия крышки — в ту схему питания, где его меняли, и откат
/// при любом сбое включения. Живой план питания юнитами не трогаем — фейк IPowerPlan.
/// </summary>
public sealed class AwakeModeTests
{
    public AwakeModeTests() => Log.Enabled = false; // сбои пишут в лог — не сорим в реальный файл

    private static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighPerf = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private const uint Sleep = 1; // «сон» — типичное исходное действие крышки

    private sealed class FakePlan : AwakeMode.IPowerPlan
    {
        public Guid Active = Balanced;
        public readonly Dictionary<Guid, uint> LidAc = new() { [Balanced] = Sleep, [HighPerf] = Sleep };
        public bool FailActive, FailRead, FailWrite, FailSet;

        public bool TryGetActive(out Guid scheme) { scheme = Active; return !FailActive; }
        public bool TryReadLidAc(Guid scheme, out uint value) => LidAc.TryGetValue(scheme, out value) && !FailRead;
        public bool WriteLidAc(Guid scheme, uint value)
        {
            if (FailWrite) return false;
            LidAc[scheme] = value;
            return true;
        }
        public bool SetActive(Guid scheme) => !FailSet;
    }

    private sealed class FakeAwake : AwakeMode.IKeepAwake
    {
        public bool Refuse;
        public bool Held;
        public bool? Display;   // с каким флагом экрана держали последний раз

        public bool Hold(bool display)
        {
            if (Refuse) return false;
            Held = true;
            Display = display;
            return true;
        }

        public void Release() => Held = false;
    }

    private static FakeAwake Ok => new();
    private static FakeAwake Refused => new() { Refuse = true };

    [Fact]
    public void Включение_и_выключение_в_одной_схеме()
    {
        var cfg = new AppConfig();
        var plan = new FakePlan();

        AwakeMode.Enable(cfg, plan, Ok).Should().BeTrue();
        plan.LidAc[Balanced].Should().Be(AwakeMode.LidDoNothing);
        cfg.AwakeSavedLidAc.Should().Be((int)Sleep);
        cfg.AwakeSavedLidScheme.Should().Be(Balanced);

        AwakeMode.Disable(cfg, plan, Ok);
        plan.LidAc[Balanced].Should().Be(Sleep);
        cfg.AwakeSavedLidAc.Should().BeNull();
        cfg.AwakeSavedLidScheme.Should().BeNull();
    }

    [Fact]
    public void Схему_сменили_при_включённой_сове_крышка_возвращается_в_исходную()
    {
        // Сама ошибка: возвращали в ставшую активной — исходная оставалась с «ничего не делать»
        var cfg = new AppConfig();
        var plan = new FakePlan();
        AwakeMode.Enable(cfg, plan, Ok);

        plan.Active = HighPerf;
        AwakeMode.Disable(cfg, plan, Ok);

        plan.LidAc[Balanced].Should().Be(Sleep, "крышку правили в «Сбалансированной» — туда и возвращаем");
        plan.LidAc[HighPerf].Should().Be(Sleep, "чужую схему не трогаем");
        cfg.AwakeSavedLidAc.Should().BeNull();
    }

    [Fact]
    public void Повторное_включение_в_другой_схеме_сначала_возвращает_старую()
    {
        // Прошлый возврат не удался (запись осталась), сову включают уже в другой схеме
        var cfg = new AppConfig();
        var plan = new FakePlan();
        AwakeMode.Enable(cfg, plan, Ok);
        plan.Active = HighPerf;

        AwakeMode.Enable(cfg, plan, Ok).Should().BeTrue();

        plan.LidAc[Balanced].Should().Be(Sleep, "старую схему вернули, прежде чем занять слот новой");
        plan.LidAc[HighPerf].Should().Be(AwakeMode.LidDoNothing);
        cfg.AwakeSavedLidScheme.Should().Be(HighPerf);
        cfg.AwakeSavedLidAc.Should().Be((int)Sleep);
    }

    [Fact]
    public void Старую_схему_вернуть_не_вышло_не_включаемся_и_запись_не_теряем()
    {
        var cfg = new AppConfig { AwakeSavedLidAc = 3, AwakeSavedLidScheme = Balanced };
        var plan = new FakePlan { Active = HighPerf, FailWrite = true };

        AwakeMode.Enable(cfg, plan, Ok).Should().BeFalse();

        cfg.AwakeSavedLidAc.Should().Be(3);
        cfg.AwakeSavedLidScheme.Should().Be(Balanced);
    }

    [Fact]
    public void Флаги_сна_не_приняты_крышка_откатывается()
    {
        var cfg = new AppConfig();
        var plan = new FakePlan();

        AwakeMode.Enable(cfg, plan, Refused).Should().BeFalse();

        plan.LidAc[Balanced].Should().Be(Sleep, "сова либо целиком, либо никак");
        cfg.AwakeSavedLidAc.Should().BeNull();
    }

    [Fact]
    public void Не_применилась_схема_крышка_откатывается()
    {
        var cfg = new AppConfig();
        var plan = new FakePlan { FailSet = true };

        AwakeMode.Enable(cfg, plan, Ok).Should().BeFalse();

        plan.LidAc[Balanced].Should().Be(Sleep);
        // откат тоже упёрся в SetActive — запись осталась, вернём на следующем старте
        cfg.AwakeSavedLidAc.Should().Be((int)Sleep);
    }

    [Fact]
    public void Исходное_не_прочиталось_не_переопределяем_вслепую()
    {
        var cfg = new AppConfig();
        var plan = new FakePlan { FailRead = true };

        AwakeMode.Enable(cfg, plan, Ok).Should().BeFalse();

        plan.LidAc[Balanced].Should().Be(Sleep);
        cfg.AwakeSavedLidAc.Should().BeNull();
    }

    [Fact]
    public void Возврат_не_удался_запись_остаётся_для_следующего_старта()
    {
        var cfg = new AppConfig();
        var plan = new FakePlan();
        AwakeMode.Enable(cfg, plan, Ok);

        plan.FailWrite = true;
        AwakeMode.Disable(cfg, plan, Ok);
        cfg.AwakeSavedLidAc.Should().Be((int)Sleep);

        plan.FailWrite = false;
        AwakeMode.RestoreLid(cfg, plan).Should().BeTrue();
        plan.LidAc[Balanced].Should().Be(Sleep);
        cfg.AwakeSavedLidAc.Should().BeNull();
    }

    [Fact]
    public void Запись_без_схемы_от_старой_версии_возвращается_в_активную()
    {
        var cfg = new AppConfig { AwakeSavedLidAc = 2 }; // до XIC-82 схему не хранили
        var plan = new FakePlan { Active = HighPerf };
        plan.LidAc[HighPerf] = AwakeMode.LidDoNothing;

        AwakeMode.RestoreLid(cfg, plan).Should().BeTrue();

        plan.LidAc[HighPerf].Should().Be(2u);
        cfg.AwakeSavedLidAc.Should().BeNull();
    }

    [Fact]
    public void Удержание_берётся_при_включении_и_отпускается_при_выключении()
    {
        // XIC-93: запрос питания вместо SetThreadExecutionState — экран держим по умолчанию
        var cfg = new AppConfig();
        var plan = new FakePlan();
        var awake = new FakeAwake();

        AwakeMode.Enable(cfg, plan, awake).Should().BeTrue();
        awake.Held.Should().BeTrue();
        awake.Display.Should().BeTrue("экран держим, пока не задан OwlIgnoreDisplay");

        AwakeMode.Disable(cfg, plan, awake);
        awake.Held.Should().BeFalse();
    }

    [Fact]
    public void OwlIgnoreDisplay_держит_только_систему()
    {
        var awake = new FakeAwake();

        AwakeMode.Enable(new AppConfig { OwlIgnoreDisplay = true }, new FakePlan(), awake).Should().BeTrue();

        awake.Display.Should().BeFalse("для удалённого доступа экран может гаснуть");
    }

    [Fact]
    public void Без_записи_возвращать_нечего() =>
        AwakeMode.RestoreLid(new AppConfig(), new FakePlan { FailActive = true }).Should().BeTrue();
}
