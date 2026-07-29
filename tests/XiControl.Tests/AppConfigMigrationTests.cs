using FluentAssertions;
using XiControl.Config;
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
}
