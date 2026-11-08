using Boo.Core;

namespace Boo.Core.Tests;

public sealed class PresenceControllerTests
{
    [Fact]
    public void Pointer_enter_reveals_and_pointer_leave_retracts()
    {
        var controller = new PresenceController();

        Assert.False(controller.IsRevealed);

        controller.SetPointerInside(true);
        Assert.True(controller.IsRevealed);

        controller.SetPointerInside(false);
        Assert.False(controller.IsRevealed);
    }

    [Fact]
    public void Capture_remains_revealed_after_pointer_leaves()
    {
        var controller = new PresenceController();

        controller.SetCaptureActive(true);
        controller.SetPointerInside(true);
        controller.SetPointerInside(false);

        Assert.True(controller.IsRevealed);
    }

    [Fact]
    public void Microphone_activity_independently_pins_reveal()
    {
        var controller = new PresenceController();

        controller.SetMicrophoneActive(true);
        Assert.True(controller.IsRevealed);

        controller.SetMicrophoneActive(false);
        Assert.False(controller.IsRevealed);
    }

    [Fact]
    public void Ending_one_activity_preserves_the_other_activity_reveal()
    {
        var controller = new PresenceController();

        controller.SetCaptureActive(true);
        controller.SetMicrophoneActive(true);
        controller.SetCaptureActive(false);

        Assert.True(controller.IsRevealed);
    }

    [Fact]
    public void Ending_final_activity_keeps_reveal_while_pointer_is_inside()
    {
        var controller = new PresenceController();

        controller.SetPointerInside(true);
        controller.SetCaptureActive(true);
        controller.SetMicrophoneActive(true);
        controller.SetCaptureActive(false);
        controller.SetMicrophoneActive(false);

        Assert.True(controller.IsRevealed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ending_overlapping_activities_in_either_order_retracts_after_final_end(bool endCaptureFirst)
    {
        var controller = new PresenceController();

        Assert.False(controller.IsRevealed);

        controller.SetCaptureActive(true);
        controller.SetMicrophoneActive(true);

        if (endCaptureFirst)
        {
            controller.SetCaptureActive(false);
            Assert.True(controller.IsRevealed);
            controller.SetMicrophoneActive(false);
        }
        else
        {
            controller.SetMicrophoneActive(false);
            Assert.True(controller.IsRevealed);
            controller.SetCaptureActive(false);
        }

        Assert.False(controller.IsRevealed);
    }
}
