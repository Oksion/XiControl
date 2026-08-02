using FluentAssertions;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Автозапуск (XIC-23) — чистая логика: чья задача и не устарел ли exe в ней.
/// Сам Планировщик (schtasks) юнитами не трогаем.
/// </summary>
public sealed class AutoStartTests
{
    private const string Sid = "S-1-5-21-3237185702-3634146915-555394186-1001";
    private const string User = @"NOTEBOOK-XIAOMI\Mi";

    // Планировщик нормализует UserId триггера в SID, а в Principal оставляет DOMAIN\User —
    // именно так выглядит задача, снятая с живой машины
    private static string TaskXml(string triggerUser, string principalUser) => $"""
        <Task version="1.2">
          <Triggers><LogonTrigger><UserId>{triggerUser}</UserId></LogonTrigger></Triggers>
          <Principals><Principal id="Author"><UserId>{principalUser}</UserId></Principal></Principals>
        </Task>
        """;

    [Fact]
    public void OwnedByCurrentUser_MatchesSid_WhenSchedulerNormalizedTrigger()
    {
        AutoStart.OwnedByCurrentUser(TaskXml(Sid, User), User, Sid).Should().BeTrue();
    }

    [Fact]
    public void OwnedByCurrentUser_MatchesDomainName_WhenOnlyPrincipalMatches()
    {
        // сверка лишь по одному формату дала бы ложное «чужая» на своей же задаче
        AutoStart.OwnedByCurrentUser(TaskXml("S-1-5-21-111-222-333-500", User), User, Sid).Should().BeTrue();
    }

    [Fact]
    public void OwnedByCurrentUser_False_ForAnotherUsersTask()
    {
        // задачу соседней учётки не трогаем никогда — иначе сломаем ей автозапуск
        string other = TaskXml("S-1-5-21-111-222-333-500", @"NOTEBOOK-XIAOMI\Guest");
        AutoStart.OwnedByCurrentUser(other, User, Sid).Should().BeFalse();
    }

    [Fact]
    public void OwnedByCurrentUser_False_OnGarbageXml()
    {
        AutoStart.OwnedByCurrentUser("не xml вовсе", User, Sid).Should().BeFalse();
    }

    [Theory]
    [InlineData("0.8.0.0", "0.9.0", true)]    // задача поднимает прошлую версию — чиним
    [InlineData("0.9.0.0", "0.9.0", false)]   // та же версия — пересоздание гоняло бы круги
    [InlineData("0.9.1.0", "0.9.0", false)]   // в задаче новее: мы старая сборка, не откатываем
    public void IsOutdated_ComparesStrictlyOlder(string taskVersion, string current, bool expected)
    {
        AutoStart.IsOutdated(taskVersion, Version.Parse(current)).Should().Be(expected);
    }

    [Theory]
    [InlineData("0.0.0.0", "0.9.0")]   // в задаче дев-сборка
    [InlineData("0.9.0.0", "0.0.0")]   // мы сами дев-сборка: иначе локальный запуск переписал бы
    [InlineData("0.0.0.0", "0.0.0")]   // чужую задачу на себя — проверено на живой машине
    public void IsOutdated_IgnoresDevBuilds(string taskVersion, string current)
    {
        AutoStart.IsOutdated(taskVersion, Version.Parse(current)).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("не версия")]
    public void IsOutdated_False_WhenVersionUnreadable(string? taskVersion)
    {
        AutoStart.IsOutdated(taskVersion, Version.Parse("0.9.0")).Should().BeFalse();
    }
}
