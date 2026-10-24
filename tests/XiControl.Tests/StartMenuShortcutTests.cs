using FluentAssertions;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Ярлык в «Пуске» (XIC-106) — чистая логика самопочинки: когда переписывать ярлык на себя.
/// Сам ярлык (IShellLink, профиль пользователя) юнитами не трогаем.
/// </summary>
public sealed class StartMenuShortcutTests
{
    // Живой IShellLink, но во временной папке, не в «Пуске»: интерфейс объявлен вручную по слотам
    // vtable, и ошибка в номере слота компилятором не ловится — вызов ушёл бы в соседний метод.
    // Запись и чтение одним и тем же объявлением сами по себе ничего не доказали бы, поэтому
    // проверяем ещё и то, что видит в ярлыке сама оболочка.
    [Fact]
    public void Write_ThenReadLink_RoundTripsTargetAndArgs()
    {
        string dir = Path.Combine(Path.GetTempPath(), "XiControlTests_" + Guid.NewGuid().ToString("N"));
        string link = Path.Combine(dir, "XiControl.lnk");
        string exe = Path.Combine(dir, "Папка с пробелом", "XiControl.exe");
        try
        {
            StartMenuShortcut.Write(exe, link);

            StartMenuShortcut.ReadLink(link).Should().Be((exe, StartMenuShortcut.ShowArg));
            // рабочая папка и аргумент лежат в .lnk строками UTF-16 (секция StringData), но не
            // обязательно с чётного смещения — читаем обе раскладки. Сам путь exe оболочка хранит
            // элементами IDList, не одной строкой, — его подтверждает ReadLink выше
            byte[] bytes = File.ReadAllBytes(link);
            string raw = System.Text.Encoding.Unicode.GetString(bytes) +
                         System.Text.Encoding.Unicode.GetString(bytes, 1, bytes.Length - 1);
            raw.Should().Contain(Path.GetDirectoryName(exe)).And.Contain(StartMenuShortcut.ShowArg);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* временная папка — не критично */ } }
    }

    [Fact]
    public void NeedsRepair_WhenTargetMissing()
    {
        // распаковали новую версию в другую папку, старую удалили — ярлык ведёт в никуда
        StartMenuShortcut.NeedsRepair(targetExists: false, targetVersion: null, Version.Parse("0.19.0"))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("0.18.0.0", "0.19.0", true)]   // ярлык поднимает прошлую версию — чиним
    [InlineData("0.19.0.0", "0.19.0", false)]  // та же версия (в т.ч. другая portable-папка) — не перехватываем
    [InlineData("0.20.0.0", "0.19.0", false)]  // в ярлыке новее: мы старая копия, не откатываем
    [InlineData("0.0.0.0", "0.19.0", false)]   // дев-сборки в сверке не участвуют — как у автозапуска
    [InlineData("0.18.0.0", "0.0.0", false)]
    [InlineData(null, "0.19.0", false)]        // версию не прочитали — чужое не переписываем
    public void NeedsRepair_WhenTargetAlive_ComparesVersions(string? targetVersion, string current, bool expected)
    {
        StartMenuShortcut.NeedsRepair(targetExists: true, targetVersion, Version.Parse(current))
            .Should().Be(expected);
    }
}
