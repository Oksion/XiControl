using FluentAssertions;
using XiControl.Ui;
using Xunit;

namespace XiControl.Tests;

public sealed class OemOsdAssetsTests
{
    private static HashSet<string> Pack(params string[] names) =>
        names.ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Resolve_UsesThemeAndNearestDpi()
    {
        var names = Pack("CapsLock_Light.png", "CapsLock_Light@175.png");

        OemOsdAssets.Resolve(names, "CapsLock", 168, dark: false, preferEnglish: false)
            .Should().Be("CapsLock_Light@175.png");
    }

    [Fact]
    public void Resolve_UsesAvailableEnglishVariant()
    {
        var names = Pack("WorkloadTurbo_Light.png", "WorkloadTurbo_En.png");

        OemOsdAssets.Resolve(names, "WorkloadTurbo", 168, dark: false, preferEnglish: true)
            .Should().Be("WorkloadTurbo_En.png");
    }

    [Fact]
    public void Resolve_MissingEnglishVariantFallsBackToLocalizedTheme()
    {
        var names = Pack("WorkloadTurbo_Light.png");

        OemOsdAssets.Resolve(names, "WorkloadTurbo", 168, dark: false, preferEnglish: true)
            .Should().Be("WorkloadTurbo_Light.png");
    }

    [Fact]
    public void Resolve_FallsBackToLocalizedOsdWhenFamilyIsMissing() =>
        OemOsdAssets.Resolve(Pack(), "CapsLock", 168, dark: false, preferEnglish: false)
            .Should().BeNull();

    [Fact]
    public void PublicLoad_MissingFamilyIsANormalFallback()
    {
        OemOsdAssets.TryLoad("__XiControl_missing__", 96, false, false, out var image)
            .Should().BeFalse();
        image.Should().BeNull();
    }

    [Fact]
    public void PublicOpen_MissingFamilyIsANormalFallback()
    {
        OemOsdAssets.TryOpen("__XiControl_missing__", 96, false, false, out var data)
            .Should().BeFalse();
        data.Should().BeNull();
    }

    [Theory]
    [InlineData(96, 100)]
    [InlineData(120, 125)]
    [InlineData(168, 175)]
    [InlineData(1000, 500)]
    public void NormalizeScale_ClampsToOemSteps(int dpi, int expected) =>
        OemOsdAssets.NormalizeScale(dpi).Should().Be(expected);

    [Theory]
    [InlineData(160, 160, 96, 160, 160)]
    [InlineData(280, 280, 168, 280, 280)]
    [InlineData(320, 320, 192, 320, 320)]
    public void Layout_DoesNotScaleHighDpiPixelsTwice(int sourceWidth, int sourceHeight, int dpi,
        int expectedWidth, int expectedHeight)
    {
        var layout = OemOsdAssets.Layout(sourceWidth, sourceHeight, dpi);

        layout.WidthDips.Should().BeApproximately(160, 0.01);
        layout.HeightDips.Should().BeApproximately(160, 0.01);
        layout.WidthPixels.Should().Be(expectedWidth);
        layout.HeightPixels.Should().Be(expectedHeight);
    }
}
