using PortableVoiceInput.Core;

namespace PortableVoiceInput.Tests;

public sealed class OverlayPlacementTests
{
    private static readonly ScreenRectangle PrimaryWorkArea = new(0, 0, 1920, 1040);

    [Theory]
    [InlineData(100, 100, 540, 156, 100, 100, 540, 156)]
    [InlineData(1800, 100, 2240, 156, 1480, 100, 1920, 156)]
    [InlineData(-100, -20, 340, 36, 0, 0, 440, 56)]
    [InlineData(1800, 1020, 2240, 1076, 1480, 984, 1920, 1040)]
    [InlineData(4000, -2000, 4440, -1944, 1480, 0, 1920, 56)]
    public void FitsInsideTheChosenMonitorWorkArea(int left, int top, int right, int bottom, int expectedLeft, int expectedTop, int expectedRight, int expectedBottom)
    {
        var actual = OverlayPlacement.Fit(new(left, top, right, bottom), PrimaryWorkArea);
        Assert.Equal(new ScreenRectangle(expectedLeft, expectedTop, expectedRight, expectedBottom), actual);
    }

    [Fact]
    public void SupportsAMonitorWithNegativeCoordinates()
    {
        var work = new ScreenRectangle(-1920, -200, 0, 840);
        Assert.Equal(new ScreenRectangle(-440, -200, 0, -144), OverlayPlacement.Fit(new(-100, -300, 340, -244), work));
        Assert.Equal(new ScreenRectangle(-1800, 100, -1360, 156), OverlayPlacement.Fit(new(-1800, 100, -1360, 156), work));
    }

    [Fact]
    public void ShrinksAnOverlayLargerThanTheAvailableArea()
    {
        var work = new ScreenRectangle(-100, 50, 200, 90);
        Assert.Equal(work, OverlayPlacement.Fit(new(10, 10, 450, 66), work));
    }

    [Fact]
    public void IgnoresInvalidWorkAreas()
    {
        var overlay = new ScreenRectangle(10, 20, 450, 76);
        Assert.Equal(overlay, OverlayPlacement.Fit(overlay, new(0, 0, 0, 0)));
    }
}
