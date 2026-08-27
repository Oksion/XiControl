using System.Reflection;
using System.Runtime.InteropServices;
using XiControl.Ui;
using Xunit;

namespace XiControl.Tests;

public sealed class NativeTrayIconTests
{
    [Fact]
    public void ShellNotifyIconUsesTheExportedWin32EntryPoint()
    {
        MethodInfo? method = typeof(NativeTrayIcon).GetMethod(
            "ShellNotifyIconW", BindingFlags.NonPublic | BindingFlags.Static);
        DllImportAttribute? import = method?.GetCustomAttribute<DllImportAttribute>();

        Assert.NotNull(import);
        Assert.Equal("Shell_NotifyIconW", import.EntryPoint);
        Assert.True(import.ExactSpelling);
    }

    [Fact]
    public void CallbackReadsNotificationFromLowWordEvenWhenVersionNegotiationWasInconclusive()
    {
        var packed = new IntPtr(unchecked((int)0x1234007B));

        Assert.Equal(0x007Bu, NativeTrayIcon.NotificationCode(packed, version4: true));
        Assert.Equal(0x007Bu, NativeTrayIcon.NotificationCode(packed, version4: false));
    }

    [Theory]
    [InlineData(0x007B)]
    [InlineData(0x0204)]
    [InlineData(0x0205)]
    public void ContextClickAcceptsVersion4AndBothLegacyButtonNotifications(uint notification)
    {
        Assert.True(NativeTrayIcon.IsContextNotification(notification));
    }

    [Theory]
    [InlineData(0x0400)]
    [InlineData(0x0401)]
    [InlineData(0x0202)]
    public void ActivationAcceptsKeyboardVersion4AndLegacyNotifications(uint notification)
    {
        Assert.True(NativeTrayIcon.IsActivationNotification(notification));
    }

    [Fact]
    public void DuplicateShellNotificationsForOnePhysicalClickAreCollapsed()
    {
        var gate = new TrayCallbackGate();

        Assert.True(gate.TryEnter(1_000));
        Assert.False(gate.TryEnter(1_020));
        Assert.True(gate.TryEnter(1_000 + TrayCallbackGate.DuplicateWindowMs));
    }
}
