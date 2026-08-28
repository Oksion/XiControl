using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using XiControl.Localization;

namespace XiControl.Ui;

/// <summary>WinUI startup error surface used before the tray application can be created.</summary>
internal sealed class StartupErrorWindow : FlyoutWindow
{
    private bool _dismissed;

    public StartupErrorWindow(string title, string message)
        : base(alwaysOnTop: true, hideFromTaskbar: false)
    {
        Title = title;
        var header = new Grid
        {
            Height = 42,
            Padding = new Thickness(18, 4, 8, 2),
            ColumnSpacing = 8,
        };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var windowClose = WindowChrome.Button(WindowChrome.CloseGlyph, Loc.T("panel.close"), Hide, close: true);
        Grid.SetColumn(windowClose, 1);
        header.Children.Add(windowClose);

        var close = new Button
        {
            Content = "OK",
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 88,
            Height = 36,
            CornerRadius = new CornerRadius(WinUiRadii.Control),
        };
        close.Click += (_, _) => Hide();

        var content = new StackPanel { Spacing = 22 };
        content.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Children =
            {
                new Border
                {
                    Width = 44,
                    Height = 44,
                    CornerRadius = new CornerRadius(22),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 216, 59, 73)),
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = new FontIcon
                    {
                        Glyph = "\uEA39",
                        FontSize = 25,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 216, 59, 73)),
                    },
                },
                new TextBlock
                {
                    Text = message,
                    FontSize = 14,
                    LineHeight = 22,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420,
                },
            },
        });
        content.Children.Add(close);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(header);
        var body = new Border
        {
            Padding = new Thickness(24, 20, 24, 22),
            Child = content,
        };
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var surface = new Border
        {
            RequestedTheme = FlyoutPalette.RequestedTheme,
            Background = new SolidColorBrush(FlyoutPalette.Card),
            CornerRadius = new CornerRadius(WinUiRadii.Overlay),
            Child = root,
        };
        Content = surface;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(header);
    }

    public Action? Dismissed { get; set; }

    public void Popup()
    {
        const int widthDips = 520;
        const int heightDips = 220;
        Rectangle work = ScreenMetrics.WorkingAreaAtCursor();
        Size physical = PhysicalSizeForDips(work, widthDips, heightDips);
        int width = Math.Min(physical.Width, Math.Max(1, work.Width - 32));
        int height = Math.Min(physical.Height, Math.Max(1, work.Height - 32));
        ShowAt(work.Left + (work.Width - width) / 2, work.Top + (work.Height - height) / 2, width, height);
    }

    protected override void OnHidden()
    {
        if (_dismissed) return;
        _dismissed = true;
        Dismissed?.Invoke();
    }
}
