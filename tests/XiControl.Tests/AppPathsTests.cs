using FluentAssertions;
using XiControl.Config;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Выбор каталога данных (XIC-34): портативный режим рядом с exe против стандартного
/// %APPDATA%. Логика чистая — реальные папки не трогаем, наличие файлов и право записи
/// подаются делегатами.
/// </summary>
public sealed class AppPathsTests
{
    private const string ExeDir = @"D:\Soft\XiControl";
    private const string AppData = @"C:\Users\Me\AppData\Roaming\XiControl";

    private static Func<string, bool> Files(params string[] existing) =>
        path => existing.Contains(path, StringComparer.OrdinalIgnoreCase);

    private static AppPaths.Resolution Resolve(Func<string, bool> files, bool writable = true) =>
        AppPaths.Resolve(ExeDir, AppData, files, _ => writable);

    [Fact]
    public void NothingNearExe_UsesAppData()
    {
        var r = Resolve(Files());

        r.Dir.Should().Be(AppData);
        r.Portable.Should().BeFalse();
        r.FallbackReason.Should().BeNull("это штатный путь, а не откат");
    }

    [Fact]
    public void ConfigNearExe_IsPortable_WithoutAnyMarker()
    {
        // пользователь сам положил конфиг рядом — этого достаточно
        var r = Resolve(Files(Path.Combine(ExeDir, "config.json")));

        r.Dir.Should().Be(ExeDir);
        r.Portable.Should().BeTrue();
    }

    [Theory]
    [InlineData(".portable")]     // как в VS Code / Windows Terminal
    [InlineData("portable.txt")]  // Проводник не даёт создать имя, начинающееся с точки
    public void Marker_EnablesPortable(string marker)
    {
        var r = Resolve(Files(Path.Combine(ExeDir, marker)));

        r.Dir.Should().Be(ExeDir);
        r.Portable.Should().BeTrue();
    }

    [Fact]
    public void ReadOnlyProgramDir_FallsBackToAppData_WithReason()
    {
        // установка в Program Files / WinGet\Packages: молча терять настройки нельзя
        var r = Resolve(Files(Path.Combine(ExeDir, "portable.txt")), writable: false);

        r.Dir.Should().Be(AppData);
        r.Portable.Should().BeFalse();
        r.FallbackReason.Should().NotBeNull("причина отката должна попасть в лог");
    }

    [Fact]
    public void UnknownExeDir_UsesAppData()
    {
        var r = AppPaths.Resolve(null, AppData, Files(), _ => true);

        r.Dir.Should().Be(AppData);
        r.Portable.Should().BeFalse();
    }

    [Fact]
    public void WriteProbe_NotRunForOrdinaryLaunch()
    {
        // без метки и конфига проверять право записи незачем — лишний файл в чужом каталоге
        bool probed = false;
        AppPaths.Resolve(ExeDir, AppData, Files(), _ => { probed = true; return true; });

        probed.Should().BeFalse();
    }
}
