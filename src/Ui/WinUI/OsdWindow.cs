using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using XiControl.Localization;

namespace XiControl.Ui;

internal sealed class OsdWindow : FlyoutWindow
{
    private readonly Border _card;
    private readonly FontIcon _icon;
    private readonly Grid _iconHost;
    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;
    private readonly TextBlock _badge;
    private readonly DispatcherQueueTimer _timer;

    public OsdWindow()
        : base(cornerRadiusDips: WinUiRadii.Overlay)
    {
        Title = Loc.T("app.name");
        _icon = new FontIcon { FontSize = 32, HorizontalAlignment = HorizontalAlignment.Center };
        _iconHost = new Grid
        {
            Width = 44,
            Height = 44,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { _icon },
        };
        _title = new TextBlock
        {
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextAlignment = TextAlignment.Left,
            TextWrapping = TextWrapping.Wrap,
        };
        _subtitle = new TextBlock
        {
            FontSize = 12.5,
            Opacity = 0.68,
            TextAlignment = TextAlignment.Left,
            TextWrapping = TextWrapping.Wrap,
        };
        _badge = new TextBlock
        {
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed,
        };

        var copy = new StackPanel
        {
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        copy.Children.Add(_title);
        copy.Children.Add(_subtitle);
        copy.Children.Add(_badge);

        var content = new Grid { ColumnSpacing = 14 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.Children.Add(_iconHost);
        Grid.SetColumn(copy, 1);
        content.Children.Add(copy);
        _card = new Border
        {
            Padding = new Thickness(18, 14, 20, 14),
            CornerRadius = new CornerRadius(WinUiRadii.Overlay),
            BorderThickness = new Thickness(0),
            Child = content,
        };
        Content = _card;
        ApplyTheme();

        _timer = DispatcherQueue.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => Hide();
    }

    public int DurationMs { get; set; } = 2800;

    public void Flash(OsdKind kind, string title, string? subtitle = null, ChargeBadge badge = ChargeBadge.None)
    {
        ApplyTheme();
        SetIcon(kind);
        _title.Text = title;
        _subtitle.Text = subtitle ?? string.Empty;
        _subtitle.Visibility = string.IsNullOrWhiteSpace(subtitle) ? Visibility.Collapsed : Visibility.Visible;
        _badge.Text = badge switch
        {
            ChargeBadge.NoPd => Loc.T("osd.charger.nopd"),
            ChargeBadge.Slow => "!",
            _ => string.Empty,
        };
        _badge.Visibility = badge == ChargeBadge.None ? Visibility.Collapsed : Visibility.Visible;

        const int widthDips = 304, heightDips = 112;
        Rectangle work = ScreenMetrics.WorkingAreaAtCursor();
        Size physical = PhysicalSizeForDips(work, widthDips, heightDips);
        int width = physical.Width;
        int height = physical.Height;
        Point at = OsdPlacement.BottomCenter(work, new Size(width, height));
        ShowAt(at.X, at.Y, width, height, activate: false);
        _timer.Stop();
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(800, DurationMs));
        _timer.Start();
    }

    private void SetIcon(OsdKind kind)
    {
        _iconHost.Children.Clear();
        if (kind is OsdKind.RefreshRate or OsdKind.RefreshRateOff)
        {
            _iconHost.Children.Add(WinUiSvgIcon.Create(
                kind == OsdKind.RefreshRate ? SvgIcons.RefreshRate : SvgIcons.RefreshRateOff, 44));
            return;
        }
        _icon.Glyph = Glyph(kind);
        _icon.Foreground = new SolidColorBrush(Accent(kind));
        _iconHost.Children.Add(_icon);
    }

    public override void Dispose()
    {
        _timer.Stop();
        base.Dispose();
    }

    private void ApplyTheme()
    {
        _card.RequestedTheme = FlyoutPalette.RequestedTheme;
        _card.Background = new SolidColorBrush(FlyoutPalette.Card);
    }

    private static string Glyph(OsdKind kind) => kind switch
    {
        OsdKind.Charging or OsdKind.ChargingLimited or OsdKind.CareOn => "\uE83E",
        OsdKind.OnBattery or OsdKind.CareOff => "\uE850",
        OsdKind.Eco => "\uE8BE",
        OsdKind.Quiet => "\uE708",
        OsdKind.Auto => "\uE9D9",
        OsdKind.Turbo => "\uE945",
        OsdKind.Full => "\uE945",
        OsdKind.MicOn => "\uE720",
        OsdKind.MicOff => "\uF781",
        OsdKind.RefreshRate or OsdKind.RefreshRateOff => "\uE895",
        OsdKind.Travel or OsdKind.TravelOff => "\uE709",
        OsdKind.TouchpadOn or OsdKind.TouchpadOff => "\uEFA5",
        OsdKind.TouchscreenOn or OsdKind.TouchscreenOff => "\uE7C9",
        OsdKind.Error => "\uEA39",
        _ => "\uE7F4",
    };

    private static Windows.UI.Color Accent(OsdKind kind) => kind switch
    {
        OsdKind.Eco or OsdKind.CareOn => Windows.UI.Color.FromArgb(255, 72, 189, 112),
        OsdKind.Turbo or OsdKind.Travel => Windows.UI.Color.FromArgb(255, 255, 158, 74),
        OsdKind.Full or OsdKind.Error => Windows.UI.Color.FromArgb(255, 255, 92, 104),
        OsdKind.Quiet => Windows.UI.Color.FromArgb(255, 125, 160, 185),
        _ => Windows.UI.Color.FromArgb(255, 74, 163, 255),
    };
}

internal sealed class OemOsdWindow : IDisposable
{
    private readonly LayeredImageWindow _window = new();
    private readonly DispatcherQueueTimer _timer;

    public OemOsdWindow()
    {
        DispatcherQueue queue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("OEM OSD must be created on the WinUI thread.");
        _timer = queue.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => _window.Hide();
    }

    public int DurationMs { get; set; } = 2800;

    public void Flash(string family)
    {
        try
        {
            Rectangle work = ScreenMetrics.WorkingAreaAtCursor();
            int dpi = _window.DpiForArea(work);
            bool english = !Loc.Current.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            using Bitmap image = OemOsdAssets.Load(family, dpi, FlyoutPalette.Dark, english);
            Point at = OsdPlacement.BottomCenter(work, image.Size);
            _window.Show(image, at);
            _timer.Stop();
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(800, DurationMs));
            _timer.Start();
        }
        catch (Exception ex)
        {
            Log.Ex($"OemOsd.{family}", ex);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _window.Dispose();
    }
}
