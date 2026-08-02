using FluentAssertions;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Проверка обновлений (XIC-20) — чистая логика: разбор тега, «пора ли идти в сеть»,
/// «показывать ли тост». Сам HTTPS-запрос к GitHub юнитами не трогаем.
/// </summary>
public sealed class UpdateCheckTests
{
    private static readonly DateTime Now = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("v0.9.0", "0.9.0")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("0.10.0", "0.10.0")]
    [InlineData(" v0.9.1 ", "0.9.1")]
    public void ParseTag_StripsPrefix(string tag, string expected)
    {
        UpdateCheck.ParseTag(tag).Should().Be(Version.Parse(expected));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pre")]        // скользящий pre-release из main
    [InlineData("v")]
    [InlineData("релиз")]
    public void ParseTag_Null_OnGarbage(string? tag)
    {
        UpdateCheck.ParseTag(tag).Should().BeNull();
    }

    [Fact]
    public void DueForCheck_False_WhenDisabled()
    {
        // выключенный тумблер = ноль исходящих запросов, это его главный смысл
        UpdateCheck.DueForCheck(false, null, Now).Should().BeFalse();
        UpdateCheck.DueForCheck(false, Now.AddDays(-30), Now).Should().BeFalse();
    }

    [Fact]
    public void DueForCheck_True_OnFirstEverCheck()
    {
        UpdateCheck.DueForCheck(true, null, Now).Should().BeTrue();
    }

    [Theory]
    [InlineData(-25, true)]   // сутки прошли
    [InlineData(-24, true)]
    [InlineData(-23, false)]  // приложение перезапускают часто — не лупим по GitHub
    [InlineData(-1, false)]
    public void DueForCheck_RespectsDailyWindow(int hoursAgo, bool expected)
    {
        UpdateCheck.DueForCheck(true, Now.AddHours(hoursAgo), Now).Should().Be(expected);
    }

    [Fact]
    public void DueForCheck_True_WhenClockWentBackwards()
    {
        // метка из будущего (перевели часы / правка конфига) не должна залипнуть навсегда
        UpdateCheck.DueForCheck(true, Now.AddDays(5), Now).Should().BeTrue();
    }

    [Fact]
    public void IsNewer_False_ForSameVersion()
    {
        // иначе на свежей установке «О программе» писала бы «доступна 0.9.0» при стоящей 0.9.0
        UpdateCheck.IsNewer(Version.Parse("0.9.0"), Version.Parse("0.9.0")).Should().BeFalse();
    }

    [Fact]
    public void IsNewer_IgnoresSkippedVersion_UnlikeShouldNotify()
    {
        // пропущенная версия гасит только тост — отметка на «О программе» обязана остаться
        var latest = Version.Parse("0.10.0");
        var current = Version.Parse("0.9.0");
        UpdateCheck.IsNewer(latest, current).Should().BeTrue();
        UpdateCheck.ShouldNotify(latest, current, "0.10.0").Should().BeFalse();
    }

    [Fact]
    public void ShouldNotify_True_WhenNewerReleaseAppears()
    {
        UpdateCheck.ShouldNotify(Version.Parse("0.10.0"), Version.Parse("0.9.0"), null).Should().BeTrue();
    }

    [Theory]
    [InlineData("0.9.0")]   // та же версия
    [InlineData("0.8.0")]   // на GitHub старее — мы бежим впереди релиза
    public void ShouldNotify_False_WhenNotNewer(string latest)
    {
        UpdateCheck.ShouldNotify(Version.Parse(latest), Version.Parse("0.9.0"), null).Should().BeFalse();
    }

    [Fact]
    public void ShouldNotify_False_ForDevBuild()
    {
        // у дев-сборки версия 0.0.0 — иначе разработка тонула бы в уведомлениях
        UpdateCheck.ShouldNotify(Version.Parse("0.10.0"), Version.Parse("0.0.0"), null).Should().BeFalse();
    }

    [Theory]
    [InlineData("0.10.0")]
    [InlineData("v0.10.0")]  // в конфиге может лежать и с префиксом
    public void ShouldNotify_False_ForAlreadyShownVersion(string skipped)
    {
        UpdateCheck.ShouldNotify(Version.Parse("0.10.0"), Version.Parse("0.9.0"), skipped).Should().BeFalse();
    }

    [Fact]
    public void ShouldNotify_True_ForVersionAfterTheSkippedOne()
    {
        // пропустили 0.10.0 — про 0.11.0 сказать обязаны
        UpdateCheck.ShouldNotify(Version.Parse("0.11.0"), Version.Parse("0.9.0"), "0.10.0").Should().BeTrue();
    }

    [Fact]
    public void ShouldNotify_False_WhenVersionUnknown()
    {
        UpdateCheck.ShouldNotify(null, Version.Parse("0.9.0"), null).Should().BeFalse();
        UpdateCheck.ShouldNotify(Version.Parse("0.10.0"), null, null).Should().BeFalse();
    }
}
