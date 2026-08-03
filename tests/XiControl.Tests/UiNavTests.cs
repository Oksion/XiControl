using FluentAssertions;
using XiControl.Ui;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Навигация по UI без окон и рендера (XIC-9): порядок обхода ячеек панели, движение
/// фокуса стрелками, выбор вкладки настроек. Сам рендер по-прежнему проверяется глазами.
/// </summary>
public sealed class UiNavTests
{
    // 16 = «в дорогу», 10/11 = пороги заряда, 19/14/12 = шестерёнка/монитор/крестик
    private static readonly int[] AlwaysPresent = [16, 10, 11, 19, 14, 12];

    [Fact]
    public void PanelOrder_ModesFirst_ThenBottomRow_ThenHeader()
    {
        var order = UiNav.PanelOrder(modes: 3, touchscreen: true, touchpad: true, hz: true, awake: true);

        order.Should().Equal(0, 1, 2, 16, 10, 11, 18, 17, 15, 13, 19, 14, 12);
    }

    [Fact]
    public void PanelOrder_HiddenCellsAreSkipped()
    {
        // фичи выключены — ячеек нет, и стрелка не должна на них попадать
        var order = UiNav.PanelOrder(modes: 5, touchscreen: false, touchpad: false, hz: false, awake: false);

        order.Should().Equal(0, 1, 2, 3, 4, 16, 10, 11, 19, 14, 12);
        order.Should().NotContain([13, 15, 17, 18]);
    }

    [Fact]
    public void PanelOrder_AlwaysKeepsCoreCells()
    {
        // даже при полностью урезанной панели заряд, «в дорогу» и кнопки шапки достижимы с клавиатуры
        var order = UiNav.PanelOrder(modes: 0, touchscreen: false, touchpad: false, hz: false, awake: false);

        order.Should().Equal(AlwaysPresent);
    }

    [Fact]
    public void PanelOrder_HasNoDuplicates()
    {
        var order = UiNav.PanelOrder(modes: 5, touchscreen: true, touchpad: true, hz: true, awake: true);

        order.Should().OnlyHaveUniqueItems("дубль означал бы, что одна ячейка ловит фокус дважды подряд");
    }

    [Theory]
    [InlineData(-1, 0)]  // фокуса не было — вперёд на первую
    [InlineData(0, 1)]
    [InlineData(8, 9)]
    [InlineData(9, 0)]   // с последней — по кругу на первую
    public void NextFocus_Forward_WrapsAround(int focus, int expected) =>
        UiNav.NextFocus(focus, count: 10, forward: true).Should().Be(expected);

    [Theory]
    [InlineData(-1, 9)]  // фокуса не было — назад на последнюю
    [InlineData(0, 9)]   // с первой — по кругу на последнюю
    [InlineData(9, 8)]
    [InlineData(1, 0)]
    public void NextFocus_Backward_WrapsAround(int focus, int expected) =>
        UiNav.NextFocus(focus, count: 10, forward: false).Should().Be(expected);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NextFocus_EmptyOrder_HasNoFocus(bool forward) =>
        UiNav.NextFocus(focus: 3, count: 0, forward)
            .Should().Be(-1, "пустой обход не должен делить на ноль");

    [Fact]
    public void KeepFocus_ResetsWhenLayoutShrank() =>
        UiNav.KeepFocus(focus: 11, count: 8)
            .Should().Be(-1, "иначе кольцо фокуса рисовалось бы вокруг несуществующей ячейки");

    [Theory]
    [InlineData(0, 8, 0)]
    [InlineData(7, 8, 7)]
    [InlineData(-1, 8, -1)]  // «фокуса нет» — валидное состояние, не трогаем
    public void KeepFocus_LeavesValidIndex(int focus, int count, int expected) =>
        UiNav.KeepFocus(focus, count).Should().Be(expected);

    [Theory]
    [InlineData(0, 8, 0)]
    [InlineData(7, 8, 7)]
    [InlineData(8, 8, 0)]   // вкладка исчезла — на первую
    [InlineData(99, 8, 0)]
    [InlineData(-1, 8, 0)]  // мусорный индекс не должен уйти в _panes[-1]
    public void ClampTab_KeepsIndexInRange(int tab, int count, int expected) =>
        UiNav.ClampTab(tab, count).Should().Be(expected);
}
