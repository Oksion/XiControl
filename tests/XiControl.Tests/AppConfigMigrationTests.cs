using FluentAssertions;
using XiControl.Config;
using XiControl.Wmi;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// AppConfig.MigrateKeyActions() — чистый маппинг legacy-полей клавиш в новые действия
/// (AppConfig.cs:248). На диск не пишет; идеальная мишень для юнитов (план 3.1).
/// </summary>
public sealed class AppConfigMigrationTests
{
    [Fact]
    public void FreshConfig_GetsDocumentedDefaults()
    {
        var cfg = new AppConfig();

        cfg.MigrateKeyActions();

        cfg.MiClickAction.Should().Be("modes");
        cfg.MiDoubleAction.Should().Be("charge");   // MiDoubleClick=true по умолчанию
        cfg.MiHoldAction.Should().Be("panel");      // до XIC-28 удержание было зашито на панель
        cfg.SettingsKeyAction.Should().Be("charge");
        cfg.AiKeyAction.Should().Be("copilot");
        cfg.ProjKeyAction.Should().Be("projection");
    }

    [Fact]
    public void OwlIgnoreDisplay_DefaultsToOff_SoOwlKeepsHoldingTheScreen()
        => new AppConfig().OwlIgnoreDisplay.Should().BeFalse(
            "поле только для тех, кто добавит его руками; без него сова ведёт себя как раньше");

    [Fact]
    public void LegacyChargeFirst_MapsClickToChargeAndDoubleToModes()
    {
        var cfg = new AppConfig { MiShortPress = "charge" };

        cfg.MigrateKeyActions();

        cfg.MiClickAction.Should().Be("charge");
        cfg.MiDoubleAction.Should().Be("modes");
    }

    [Fact]
    public void ExistingConfigWithoutHoldAction_KeepsPanelBehaviour()
    {
        // конфиг «из прошлой версии»: клик/двойной уже заданы, про удержание он не знает
        var cfg = new AppConfig { MiClickAction = "modes", MiDoubleAction = "none" };

        cfg.MigrateKeyActions();

        cfg.MiHoldAction.Should().Be("panel", "поведение удержания не должно измениться");
    }

    [Fact]
    public void ExplicitHoldAction_SurvivesMigration()
    {
        var cfg = new AppConfig { MiHoldAction = "travel" };

        cfg.MigrateKeyActions();

        cfg.MiHoldAction.Should().Be("travel");
    }

    [Fact]
    public void LegacyDoubleClickDisabled_MapsDoubleActionToNone()
    {
        var cfg = new AppConfig { MiDoubleClick = false };

        cfg.MigrateKeyActions();

        cfg.MiDoubleAction.Should().Be("none");
    }

    [Fact]
    public void LegacyAiProgramWithSpaces_BecomesQuotedLaunchCommand()
    {
        var cfg = new AppConfig
        {
            AiKeyProgram = @"C:\Program Files\App\ai.exe",
            AiKeyArgs = "--go",
        };

        cfg.MigrateKeyActions();

        cfg.AiKeyAction.Should().Be("launch");
        cfg.AiKeyCommand.Should().Be("\"C:\\Program Files\\App\\ai.exe\" --go");
    }

    [Fact]
    public void AlreadySetActions_AreNotOverwritten()
    {
        var cfg = new AppConfig
        {
            MiClickAction = "touchpad",
            MiDoubleAction = "settings",
            SettingsKeyAction = "modes",
            AiKeyAction = "none",
            ProjKeyAction = "charge",
        };

        cfg.MigrateKeyActions();

        cfg.MiClickAction.Should().Be("touchpad");
        cfg.MiDoubleAction.Should().Be("settings");
        cfg.SettingsKeyAction.Should().Be("modes");
        cfg.AiKeyAction.Should().Be("none");
        cfg.ProjKeyAction.Should().Be("charge");
    }

    // ---- Видимость режимов: старые тумблеры EcoMode/FullSpeedMode → HiddenModes ----

    // Человек, скрывший Эко год назад, не должен увидеть его снова после обновления.
    [Fact]
    public void СтарыеТумблерыВидимости_ПереносятсяВHiddenModes()
    {
        var cfg = new AppConfig { EcoMode = false, FullSpeedMode = false };

        cfg.MigrateKeyActions();

        cfg.HiddenModes.Should().Contain([PerfMode.Eco, PerfMode.FullSpeed]);
    }

    // Balance по умолчанию ВИДЕН. Прошивка Book Pro 14 его не принимает, но пряча его дефолтом
    // мы ломали Redmi Book Pro 15 2022, где он — один из двух рабочих режимов: скрытый режим не
    // участвует в переборе, значит отказа не случится, и автоопределение до него не доберётся
    // никогда. Набор режимов определяет прошивка, а не наш дефолт (XIC-57).
    [Fact]
    public void Balance_ПоУмолчаниюВиден()
    {
        var cfg = new AppConfig();

        cfg.MigrateKeyActions();

        cfg.HiddenModes.Should().BeEmpty();
    }

    [Fact]
    public void УжеМигрированныйКонфиг_НеТрогаем()
    {
        var cfg = new AppConfig { EcoMode = false, HiddenModes = [PerfMode.Turbo] };

        cfg.MigrateKeyActions();

        cfg.HiddenModes.Should().Equal([PerfMode.Turbo], "выбор человека важнее старых тумблеров");
    }
}
