using Boo.Core;
using Xunit;

namespace Boo.Core.Tests;

public sealed class GeometryRegressionTests
{
    private static readonly Guid ObservationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset CapturedAt = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Full_resolution_frame_preserves_physical_coordinates_at_any_dpi_scale()
    {
        var display = new DisplayInfo("left", "Left", new RectD(-1920, -200, 1920, 1080), 1.5);
        var context = Context(display, frameWidth: 1920, frameHeight: 1080);

        var actual = AnnotationValidator.MapToDisplay(new RectD(100, 50, 200, 100), context);

        Assert.Equal(new RectD(-1820, -150, 200, 100), actual);
    }

    [Fact]
    public void Downsampled_frame_uses_frame_to_display_ratio_independent_of_dpi_scale()
    {
        var display = new DisplayInfo("left", "Left", new RectD(-1920, -200, 1920, 1080), 1.25);
        var context = Context(display, frameWidth: 960, frameHeight: 540);

        var actual = AnnotationValidator.MapToDisplay(new RectD(100, 50, 200, 100), context);

        Assert.Equal(new RectD(-1720, -100, 400, 200), actual);
    }

    private static ScreenContext Context(DisplayInfo display, int frameWidth, int frameHeight)
    {
        return new ScreenContext(
            ObservationId,
            Generation: 17,
            display,
            frameWidth,
            frameHeight,
            new WindowIdentity(123, 456, "fixture", new RectD(10, 20, 400, 300)),
            CapturedAt);
    }
}
