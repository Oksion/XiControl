using FluentAssertions;
using XiControl.Ui;
using Xunit;

namespace XiControl.Tests;

/// <summary>Модель WinUI tray-меню фиксирует семантику разделителей и вложенных режимов.</summary>
public sealed class TrayMenuStyleTests
{
    [Fact]
    public void Separator_HasNoCommandOrChildren()
    {
        TrayMenuEntry separator = TrayMenuEntry.Separator();

        separator.Id.Should().Be(0);
        separator.Text.Should().BeEmpty();
        separator.Children.Should().BeNull();
        separator.IsSeparator.Should().BeTrue();
    }

    [Fact]
    public void MenuItem_DefaultsToEnabledUncheckedLeaf()
    {
        TrayMenuEntry item = new(42, "Settings");

        item.Enabled.Should().BeTrue();
        item.Checked.Should().BeFalse();
        item.Children.Should().BeNull();
    }

    [Fact]
    public void MenuItem_CarriesCheckedEnabledAndChildrenState()
    {
        TrayMenuEntry child = new(101, "Auto", Checked: true);
        TrayMenuEntry parent = new(20, "Performance", Enabled: false, Children: [child]);

        parent.Enabled.Should().BeFalse();
        parent.Children.Should().ContainSingle().Which.Should().BeSameAs(child);
        child.Checked.Should().BeTrue();
    }

    [Fact]
    public void Group_IsHeaderlessAndKeepsItsChildren()
    {
        TrayMenuEntry child = new(10, "Settings");

        TrayMenuEntry group = TrayMenuEntry.Group([child]);

        group.IsGroup.Should().BeTrue();
        group.IsSeparator.Should().BeFalse();
        group.Text.Should().BeEmpty();
        group.Children.Should().ContainSingle().Which.Should().BeSameAs(child);
    }

    [Fact]
    public void ReferenceLayout_MeasuresToCompactMenuHeight()
    {
        TrayMenuEntry[] modes = Enumerable.Range(0, 5)
            .Select(index => new TrayMenuEntry((uint)(100 + index), $"Mode {index}"))
            .ToArray();
        TrayMenuEntry[] tools = Enumerable.Range(0, 4)
            .Select(index => new TrayMenuEntry((uint)(10 + index), $"Tool {index}"))
            .ToArray();
        TrayMenuEntry[] menu =
        [
            new(1, "Charge"),
            new(2, "Travel"),
            TrayMenuEntry.Separator(),
            new(20, "Performance", Children: modes),
            TrayMenuEntry.Separator(),
            TrayMenuEntry.Group(tools),
            TrayMenuEntry.Separator(),
            new(11, "Exit"),
        ];

        TrayMenuWindow.MeasureHeight(menu).Should().Be(375);
    }
}
