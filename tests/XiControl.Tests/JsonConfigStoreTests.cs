using FluentAssertions;
using XiControl.Config;
using XiControl.Localization;
using XiControl.Wmi;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// JsonConfigStore на временной папке — реальный %APPDATA%\XiControl не трогаем.
/// </summary>
public sealed class JsonConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "XiControl.Tests." + Guid.NewGuid().ToString("N"));

    // Load битого файла зовёт Log.Ex → писал бы в реальный %APPDATA%\XiControl\log.txt
    public JsonConfigStoreTests() => Log.Enabled = false;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка, не критично */ }
    }

    [Fact]
    public void SaveThenLoad_RoundtripsValues()
    {
        var store = new JsonConfigStore(_dir);
        var cfg = new AppConfig
        {
            Language = "zh",
            ChargeCare = true,
            StartPerfMode = PerfMode.Turbo,
            MiClickAction = "touchpad",
        };

        store.Save(cfg);
        var loaded = new JsonConfigStore(_dir).Load();

        loaded.Language.Should().Be("zh");
        loaded.ChargeCare.Should().BeTrue();
        loaded.StartPerfMode.Should().Be(PerfMode.Turbo);
        loaded.MiClickAction.Should().Be("touchpad");
    }

    [Fact]
    public void Load_BindsStore_SoCfgSaveWorks()
    {
        var store = new JsonConfigStore(_dir);
        store.Save(new AppConfig());

        var cfg = store.Load();
        cfg.ChargeCare = true;
        cfg.Save();   // должен уйти в НАШ store (в _dir), а не в %APPDATA%

        new JsonConfigStore(_dir).Load().ChargeCare.Should().BeTrue();
    }

    // Регрессия: перечисления пишутся именами («Balance»), а Deserialize звался БЕЗ JsonOpts —
    // прочитать это имя он не мог, падал, и Load молча отдавал дефолты. То есть один запуск
    // стирал человеку весь конфиг. Чтение и запись обязаны ходить через одни опции.
    [Fact]
    public void ИменаПеречисленийПереживаютКругЗаписьЧтение()
    {
        var store = new JsonConfigStore(_dir);
        var cfg = store.Load();
        cfg.ChargeCare = true;
        cfg.StartPerfMode = PerfMode.Turbo;
        cfg.HiddenModes = [PerfMode.Balance, PerfMode.Eco];
        cfg.Save();

        var back = new JsonConfigStore(_dir).Load();

        back.ChargeCare.Should().BeTrue("иначе разбор упал и конфиг уехал в дефолты");
        back.StartPerfMode.Should().Be(PerfMode.Turbo);
        back.HiddenModes.Should().Equal([PerfMode.Balance, PerfMode.Eco]);
        File.ReadAllText(store.FilePath).Should().Contain("\"Balance\"",
            "config.json правят руками — там должны быть имена, а не числа");
    }

    [Fact]
    public void LegacyIntLanguage_MigratesToCultureCode()
    {
        // конфиг старого формата: язык хранился индексом enum (0=ru, 1=en, 2=zh)
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "config.json"), "{ \"Language\": 2 }");

        var cfg = new JsonConfigStore(_dir).Load();

        cfg.Language.Should().Be("zh", "старый индекс 2 должен мигрировать в культурный код");
    }

    [Fact]
    public void MissingFile_YieldsDefaultsWithMigratedKeyActions()
    {
        var cfg = new JsonConfigStore(_dir).Load();

        // дефолты + отработавшая миграция клавиш (Load всегда её зовёт)
        cfg.ChargeCare.Should().BeFalse();
        cfg.MiClickAction.Should().Be("modes");
        cfg.ProjKeyAction.Should().Be("projection");
    }

    [Fact]
    public void CorruptedFile_FallsBackToDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "config.json"), "{ это не json ]]");

        var cfg = new JsonConfigStore(_dir).Load();

        cfg.Should().NotBeNull();
        cfg.MiClickAction.Should().Be("modes"); // дефолты + миграция, без падения
    }
}
