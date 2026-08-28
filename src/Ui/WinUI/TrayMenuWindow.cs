using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace XiControl.Ui;

internal sealed record TrayMenuEntry(
    uint Id,
    string Text,
    string? Icon = null,
    bool Checked = false,
    bool Enabled = true,
    IReadOnlyList<TrayMenuEntry>? Children = null)
{
    public bool IsSeparator => Id == 0 && Children is null;
    public bool IsGroup => Id == 0 && Children is not null;
    public static TrayMenuEntry Separator() => new(0, string.Empty);
    public static TrayMenuEntry Group(IReadOnlyList<TrayMenuEntry> children) =>
        new(0, string.Empty, Children: children);
}

/// <summary>Grouped WinUI tray menu with permanently visible compact mode and tool grids.</summary>
internal sealed class TrayMenuWindow : FlyoutWindow
{
    internal const int MenuWidthDips = 280;

    private readonly StackPanel _root = new() { Spacing = 0 };
    private readonly Border _card;
    private Action<uint>? _execute;
    private int _activationGeneration;
    private int _popupGeneration;
    private long _deactivationGraceUntil;

    public TrayMenuWindow()
        : base()
    {
        Title = "XiControl";
        _card = new Border
        {
            Padding = new Thickness(7, 5, 7, 7),
            CornerRadius = new CornerRadius(WinUiRadii.Overlay),
            BorderThickness = new Thickness(0),
            Child = new ScrollViewer
            {
                Content = _root,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            },
        };
        Content = _card;
        Activated += (_, e) =>
        {
            int generation = Interlocked.Increment(ref _activationGeneration);
            if (e.WindowActivationState == WindowActivationState.Deactivated && IsVisible)
                _ = HideAfterConfirmedDeactivationAsync(generation);
        };
    }

    public void Popup(IReadOnlyList<TrayMenuEntry> items, Action<uint> execute)
    {
        int popupGeneration = Interlocked.Increment(ref _popupGeneration);
        Interlocked.Increment(ref _activationGeneration);
        Volatile.Write(ref _deactivationGraceUntil, Environment.TickCount64 + 450);
        if (IsVisible) Hide();

        _execute = execute;
        _root.Children.Clear();
        ApplyTheme();
        foreach (TrayMenuEntry item in items) Add(item);

        Rectangle work = ScreenMetrics.WorkingAreaAtCursor();
        Point cursor = ScreenMetrics.CursorPosition();
        Size physical = PhysicalSizeForDips(work, MenuWidthDips, MeasureHeight(items));
        int width = Math.Min(physical.Width, Math.Max(1, work.Width - 32));
        int height = Math.Min(physical.Height, Math.Max(1, work.Height - 32));
        int x = Math.Clamp(cursor.X, work.Left + 16, Math.Max(work.Left + 16, work.Right - width - 16));
        int y = Math.Clamp(cursor.Y, work.Top + 16, Math.Max(work.Top + 16, work.Bottom - height - 16));
        ShowAt(x, y, width, height);
        _ = SetForegroundWindow(Handle);
        Activate();
        _ = ReinforceActivationAsync(popupGeneration);
    }

    private async Task HideAfterConfirmedDeactivationAsync(int generation)
    {
        // WinUI can deliver a stale Deactivated event from the previous hide after the next
        // ShowAt. Give the matching Activated event time to arrive, then verify the foreground
        // HWND before closing the menu.
        long graceRemaining = Volatile.Read(ref _deactivationGraceUntil) - Environment.TickCount64;
        await Task.Delay((int)Math.Clamp(graceRemaining + 80, 120, 650)).ConfigureAwait(false);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != Volatile.Read(ref _activationGeneration) || !IsVisible) return;
            if (GetForegroundWindow() != Handle) Hide();
        });
    }

    private async Task ReinforceActivationAsync(int generation)
    {
        // AppWindow.Activate can lose the race to the taskbar/overflow host after a tray click.
        // Reassert foreground ownership a few times while the user-initiated activation grant is
        // still valid. A new popup invalidates all retries from the previous invocation.
        foreach (int delay in new[] { 35, 90, 180 })
        {
            await Task.Delay(delay).ConfigureAwait(false);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (generation != Volatile.Read(ref _popupGeneration) || !IsVisible) return;
                if (GetForegroundWindow() == Handle) return;
                _ = SetForegroundWindow(Handle);
                Activate();
            });
        }
    }

    private void Add(TrayMenuEntry item)
    {
        if (item.IsSeparator)
        {
            _root.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(4, 4, 4, 4),
                Background = new SolidColorBrush(FlyoutPalette.Dark
                    ? Windows.UI.Color.FromArgb(54, 126, 160, 210)
                    : Windows.UI.Color.FromArgb(48, 74, 163, 255)),
            });
            return;
        }

        if (item.Children is { } children)
        {
            _root.Children.Add(GroupPanel(item, children));
            return;
        }

        _root.Children.Add(ItemButton(item, compact: false));
    }

    private StackPanel GroupPanel(TrayMenuEntry parent, IReadOnlyList<TrayMenuEntry> children)
    {
        bool optionGroup = !parent.IsGroup;
        var panel = new StackPanel { Spacing = optionGroup ? 4 : 2 };
        if (!parent.IsGroup)
        {
            panel.Children.Add(new Border
            {
                Height = 32,
                Padding = new Thickness(9, 4, 9, 2),
                Child = RowContent(parent, compact: true),
            });
        }

        var grid = new Grid
        {
            ColumnSpacing = optionGroup ? 6 : 4,
            RowSpacing = optionGroup ? 4 : 2,
            Margin = optionGroup ? new Thickness(4, 0, 4, 1) : new Thickness(0),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < children.Count; i++)
        {
            if (i / 2 >= grid.RowDefinitions.Count)
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
            Button button = ItemButton(children[i], compact: true, option: optionGroup);
            Grid.SetColumn(button, i % 2);
            Grid.SetRow(button, i / 2);
            grid.Children.Add(button);
        }
        panel.Children.Add(grid);
        return panel;
    }

    private Button ItemButton(TrayMenuEntry item, bool compact, bool option = false)
    {
        var button = new Button
        {
            Content = RowContent(item, compact),
            IsEnabled = item.Enabled,
            Height = compact ? 34 : 40,
            Padding = compact ? new Thickness(8, 4, 8, 4) : new Thickness(9, 5, 9, 5),
            CornerRadius = new CornerRadius(WinUiRadii.Control),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(ItemBackground(item.Checked, option)),
            BorderThickness = new Thickness(0),
        };
        button.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(PointerBackground(item.Checked));
        button.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(PressedBackground(item.Checked));
        AutomationProperties.SetName(button, item.Text);
        button.Click += (_, _) =>
        {
            Hide();
            _execute?.Invoke(item.Id);
        };
        return button;
    }

    private static Grid RowContent(TrayMenuEntry item, bool compact)
    {
        var row = new Grid { ColumnSpacing = compact ? 7 : 9 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(compact ? 19 : 22) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(compact ? 15 : 18) });
        FrameworkElement icon = item.Icon is { } name
            ? WinUiSvgIcon.Create(name, compact ? 14 : 16, 64, UiIconColor())
            : new Border { Width = compact ? 14 : 16, Height = compact ? 14 : 16, Opacity = 0 };
        icon.Opacity = item.Icon is null ? 0 : 0.78;
        icon.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(icon);
        var text = new TextBlock
        {
            Text = item.Text,
            FontSize = compact ? 12.5 : 13.5,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        if (item.Checked)
        {
            var check = new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = compact ? 12 : 13,
                Foreground = new SolidColorBrush(AccentColor()),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            Grid.SetColumn(check, 2);
            row.Children.Add(check);
        }
        return row;
    }

    private void ApplyTheme()
    {
        _card.RequestedTheme = FlyoutPalette.RequestedTheme;
        _card.Background = new SolidColorBrush(FlyoutPalette.Card);
    }

    private static Windows.UI.Color ItemBackground(bool selected, bool option)
    {
        if (selected) return FlyoutPalette.Dark
            ? Windows.UI.Color.FromArgb(76, 49, 127, 220)
            : Windows.UI.Color.FromArgb(50, 28, 112, 210);
        if (!option) return Microsoft.UI.Colors.Transparent;
        return FlyoutPalette.Dark
            ? Windows.UI.Color.FromArgb(86, 56, 58, 64)
            : Windows.UI.Color.FromArgb(178, 236, 239, 244);
    }

    private static Windows.UI.Color PointerBackground(bool selected) => FlyoutPalette.Dark
        ? Windows.UI.Color.FromArgb(selected ? (byte)100 : (byte)70, 74, 151, 235)
        : Windows.UI.Color.FromArgb(selected ? (byte)70 : (byte)42, 35, 123, 220);

    private static Windows.UI.Color PressedBackground(bool selected) => FlyoutPalette.Dark
        ? Windows.UI.Color.FromArgb(selected ? (byte)118 : (byte)88, 74, 151, 235)
        : Windows.UI.Color.FromArgb(selected ? (byte)88 : (byte)60, 35, 123, 220);

    private static Windows.UI.Color AccentColor() => FlyoutPalette.Dark
        ? Windows.UI.Color.FromArgb(255, 102, 174, 255)
        : Windows.UI.Color.FromArgb(255, 25, 103, 198);

    private static Color UiIconColor() => FlyoutPalette.Dark
        ? Color.FromArgb(232, 240, 244, 248)
        : Color.FromArgb(230, 35, 42, 50);

    internal static int MeasureHeight(IEnumerable<TrayMenuEntry> items)
    {
        int height = 12;
        foreach (TrayMenuEntry item in items)
        {
            if (item.IsSeparator) height += 9;
            else if (item.Children is { } children)
            {
                int rows = (children.Count + 1) / 2;
                int rowSpacing = item.IsGroup ? 2 : 4;
                int gridHeight = rows * 34 + Math.Max(0, rows - 1) * rowSpacing;
                height += (item.IsGroup ? 0 : 36) + gridHeight;
            }
            else height += 40;
        }
        return Math.Max(140, height);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
