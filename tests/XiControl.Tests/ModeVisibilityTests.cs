using FluentAssertions;
using XiControl.Config;
using XiControl.Wmi;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Видимость режимов производительности. Правило «минимум два» здесь не украшение: набор из
/// одного режима оставляет человека с кнопкой, которая ничего не переключает, и в панели —
/// с ячейкой без альтернативы.
/// </summary>
public sealed class ModeVisibilityTests
{
    private static readonly PerfMode[] All =
        [PerfMode.Eco, PerfMode.Quiet, PerfMode.Balance, PerfMode.Auto, PerfMode.Turbo, PerfMode.FullSpeed];

    [Fact]
    public void Без_скрытых_видно_всё() =>
        ModeVisibility.Visible(All, null).Should().Equal(All);

    [Fact]
    public void Скрытые_убираются_а_порядок_сохраняется() =>
        ModeVisibility.Visible(All, [PerfMode.Eco, PerfMode.Turbo])
            .Should().Equal(PerfMode.Quiet, PerfMode.Balance, PerfMode.Auto, PerfMode.FullSpeed);

    // Ручная правка config.json может скрыть всё. Отбирать у человека выбор из-за этого нельзя —
    // такой конфиг игнорируется целиком, как и прочий мусор в настройках.
    [Theory]
    [InlineData(5)]  // остался один
    [InlineData(6)]  // не осталось ни одного
    public void Скрыто_слишком_много_значит_показываем_всё(int hideCount) =>
        ModeVisibility.Visible(All, All.Take(hideCount)).Should().Equal(All);

    [Fact]
    public void Ровно_минимум_ещё_допустим() =>
        ModeVisibility.Visible(All, All.Take(All.Length - ModeVisibility.Minimum))
            .Should().HaveCount(ModeVisibility.Minimum);

    [Theory]
    [InlineData(3, true)]
    [InlineData(2, false)]
    [InlineData(1, false)]
    public void Скрывать_можно_пока_видимых_больше_минимума(int visible, bool expected) =>
        ModeVisibility.CanHide(visible).Should().Be(expected);

    [Fact]
    public void Toggle_прячет_режим()
    {
        var hidden = ModeVisibility.Toggle(All, null, PerfMode.Turbo, visible: false);

        hidden.Should().Equal(PerfMode.Turbo);
    }

    [Fact]
    public void Toggle_возвращает_режим_обратно()
    {
        var hidden = ModeVisibility.Toggle(All, [PerfMode.Turbo, PerfMode.Eco], PerfMode.Turbo, visible: true);

        hidden.Should().Equal(PerfMode.Eco);
    }

    // Главное свойство: последний разрешённый шаг не проходит, и набор скрытых не меняется —
    // вызывающему достаточно сравнить результат, знать правило ему не нужно.
    [Fact]
    public void Toggle_не_даёт_уйти_ниже_минимума()
    {
        PerfMode[] hidden = [PerfMode.Eco, PerfMode.Quiet, PerfMode.Balance, PerfMode.Auto]; // видно Turbo и FullSpeed

        var next = ModeVisibility.Toggle(All, hidden, PerfMode.Turbo, visible: false);

        next.Should().BeEquivalentTo(hidden, "скрывать больше нечего — набор не изменился");
        ModeVisibility.Visible(All, next).Should().HaveCount(ModeVisibility.Minimum);
    }

    // Вернуть режим можно всегда, даже когда скрыть уже нельзя.
    [Fact]
    public void Вернуть_режим_можно_и_на_минимуме()
    {
        PerfMode[] hidden = [PerfMode.Eco, PerfMode.Quiet, PerfMode.Balance, PerfMode.Auto];

        var next = ModeVisibility.Toggle(All, hidden, PerfMode.Auto, visible: true);

        ModeVisibility.Visible(All, next).Should().HaveCount(3);
    }

    [Fact]
    public void Повторное_скрытие_не_плодит_дублей()
    {
        var once = ModeVisibility.Toggle(All, null, PerfMode.Eco, visible: false);
        var twice = ModeVisibility.Toggle(All, once, PerfMode.Eco, visible: false);

        twice.Should().Equal(PerfMode.Eco);
    }
}
