using FluentAssertions;
using XiControl.Ui;
using Xunit;

namespace XiControl.Tests;

public sealed class OemOsdAssetsTests
{
    [Fact]
    public void FullOemPack_IsEmbedded() =>
        OemOsdAssets.ResourceCount.Should().Be(OemOsdAssets.ExpectedResourceCount);

    [Fact]
    public void Resolve_UsesThemeAndNearestDpi() =>
        OemOsdAssets.Resolve("CapsLock", 168, dark: false, preferEnglish: false)
            .Should().Be("CapsLock_Light@175.png");

    [Fact]
    public void Resolve_UsesAvailableEnglishVariant() =>
        OemOsdAssets.Resolve("WorkloadTurbo", 168, dark: false, preferEnglish: true)
            .Should().Be("WorkloadTurbo_En.png");

    [Theory]
    [InlineData(96, 100)]
    [InlineData(120, 125)]
    [InlineData(168, 175)]
    [InlineData(1000, 500)]
    public void NormalizeScale_ClampsToOemSteps(int dpi, int expected) =>
        OemOsdAssets.NormalizeScale(dpi).Should().Be(expected);

    [Fact]
    public void Load_ReturnsIndependentBitmap()
    {
        using var image = OemOsdAssets.Load("CapsLock", 168, dark: false, preferEnglish: false);

        image.Width.Should().Be(280);
        image.Height.Should().Be(280);
    }

    [Fact]
    public void ThemedArtwork_PreservesOriginalPerPixelTransparency()
    {
        using var image = OemOsdAssets.Load("CapsUnlock", 168, dark: true, preferEnglish: false);

        image.GetPixel(0, 0).A.Should().Be(0);
        image.GetPixel(image.Width / 2, image.Height / 2).A.Should().BeLessThan(255);
    }

    [Fact]
    public void LayeredImageSurface_AcceptsTheOemAlphaBitmap()
    {
        using var window = new LayeredImageWindow();
        using var image = OemOsdAssets.Load("CapsUnlock", 168, dark: true, preferEnglish: false);

        window.Show(image, new Point(-32000, -32000));
        window.Hide();
    }

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
