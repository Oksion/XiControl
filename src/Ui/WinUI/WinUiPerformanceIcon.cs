using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using XiControl.Wmi;
using WinUIImage = Microsoft.UI.Xaml.Controls.Image;

namespace XiControl.Ui;

/// <summary>
/// WinUI counterpart of the original animated performance artwork. The source SVG layers and
/// motion profiles intentionally mirror QuickPanelWindow: leaf sway, star twinkle, gauge sweep,
/// bolt pulse and the rocket body/flame movement.
/// </summary>
internal sealed class WinUiPerformanceIcon : Grid
{
    private readonly Storyboard? _storyboard;
    private readonly List<Action> _reset = [];
    private bool _loaded;
    private bool _running;
    private bool _selected;
    private bool _pointerOver;

    public WinUiPerformanceIcon(PerfMode mode, double size)
    {
        Width = size;
        Height = size;
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;

        _storyboard = mode switch
        {
            PerfMode.Eco => BuildEco(size),
            PerfMode.Quiet => BuildQuiet(size),
            PerfMode.Auto => BuildAuto(size),
            PerfMode.Turbo => BuildTurbo(size),
            PerfMode.FullSpeed => BuildFullSpeed(size),
            _ => BuildStatic(mode, size),
        };

        Loaded += (_, _) =>
        {
            _loaded = true;
            UpdatePlayback();
        };
        Unloaded += (_, _) =>
        {
            _loaded = false;
            Stop();
        };
    }

    public void SetSelected(bool selected)
    {
        if (_selected == selected) return;
        _selected = selected;
        UpdatePlayback();
    }

    public void SetPointerOver(bool pointerOver)
    {
        if (_pointerOver == pointerOver) return;
        _pointerOver = pointerOver;
        UpdatePlayback();
    }

    private Storyboard BuildEco(double size)
    {
        WinUIImage leaf = AddImage(SvgIcons.PerfEco, size);
        var rotate = new RotateTransform();
        leaf.RenderTransformOrigin = new Windows.Foundation.Point(0.25, 0.84);
        leaf.RenderTransform = rotate;
        _reset.Add(() => rotate.Angle = 0);

        var storyboard = new Storyboard();
        storyboard.Children.Add(Animate(rotate, "Angle", -4.5, 4.5, 2.1));
        return storyboard;
    }

    private Storyboard BuildQuiet(double size)
    {
        AddImage(SvgIcons.PerfQuietMoon, size);
        WinUIImage first = AddImage(SvgIcons.PerfQuietStar1, size);
        WinUIImage second = AddImage(SvgIcons.PerfQuietStar2, size);
        _reset.Add(() => first.Opacity = 1);
        _reset.Add(() => second.Opacity = 1);

        var storyboard = new Storyboard();
        storyboard.Children.Add(Animate(first, "Opacity", 1, 0.3, 1.5));
        storyboard.Children.Add(Animate(second, "Opacity", 0.3, 1, 1.5));
        return storyboard;
    }

    private Storyboard BuildAuto(double size)
    {
        AddImage(SvgIcons.PerfAutoDial, size);
        WinUIImage needle = AddImage(SvgIcons.PerfAutoNeedle, size);
        var rotate = new RotateTransform();
        needle.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        needle.RenderTransform = rotate;
        _reset.Add(() => rotate.Angle = 0);

        var storyboard = new Storyboard();
        storyboard.Children.Add(Animate(rotate, "Angle", -40, 20, 2.25));
        return storyboard;
    }

    private Storyboard BuildTurbo(double size)
    {
        WinUIImage bolt = AddImage(SvgIcons.PerfTurbo, size);
        var scale = new ScaleTransform();
        bolt.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        bolt.RenderTransform = scale;
        _reset.Add(() =>
        {
            scale.ScaleX = 1;
            scale.ScaleY = 1;
            bolt.Opacity = 1;
        });

        var storyboard = new Storyboard();
        storyboard.Children.Add(Animate(scale, "ScaleX", 0.96, 1.05, 0.98));
        storyboard.Children.Add(Animate(scale, "ScaleY", 0.96, 1.05, 0.98));
        storyboard.Children.Add(Animate(bolt, "Opacity", 0.78, 1, 0.98));
        return storyboard;
    }

    private Storyboard BuildFullSpeed(double size)
    {
        WinUIImage flame = AddImage(SvgIcons.PerfFullFlame, size);
        WinUIImage body = AddImage(SvgIcons.PerfFullBody, size);
        var flameScale = new ScaleTransform();
        var bodyOffset = new TranslateTransform();
        flame.RenderTransformOrigin = new Windows.Foundation.Point(0.375, 0.66);
        flame.RenderTransform = flameScale;
        body.RenderTransform = bodyOffset;
        _reset.Add(() =>
        {
            flameScale.ScaleX = 1;
            flameScale.ScaleY = 1;
            flame.Opacity = 1;
            bodyOffset.X = 0;
            bodyOffset.Y = 0;
        });

        var storyboard = new Storyboard();
        storyboard.Children.Add(Animate(flameScale, "ScaleX", 0.96, 1.08, 0.7));
        storyboard.Children.Add(Animate(flameScale, "ScaleY", 0.96, 1.08, 0.7));
        storyboard.Children.Add(Animate(flame, "Opacity", 0.82, 1, 0.55));
        storyboard.Children.Add(Animate(bodyOffset, "X", -0.25, 0.25, 0.63));
        storyboard.Children.Add(Animate(bodyOffset, "Y", -0.2, 0.2, 0.47));
        return storyboard;
    }

    private Storyboard? BuildStatic(PerfMode mode, double size)
    {
        AddImage(ModeUi.SvgIcon(mode), size);
        return null;
    }

    private WinUIImage AddImage(string name, double size)
    {
        WinUIImage image = WinUiSvgIcon.Create(name, size);
        Children.Add(image);
        return image;
    }

    private static DoubleAnimation Animate(DependencyObject target, string property,
        double from, double to, double seconds)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromSeconds(seconds)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    private void UpdatePlayback()
    {
        if (!_loaded || _storyboard is null) return;
        if (_selected || _pointerOver)
        {
            if (_running) return;
            _storyboard.Begin();
            _running = true;
            return;
        }
        Stop();
    }

    private void Stop()
    {
        if (_storyboard is not null && _running) _storyboard.Stop();
        _running = false;
        foreach (Action reset in _reset) reset();
    }
}
