using Boo.Windows;

namespace Boo.Integration.Tests;

/// <summary>
/// Which of Boo's own windows a screen capture switches to "excluded from capture" for one frame. Changing that setting
/// on a hidden window left a blank white "Boo companion" frame on screen during the focus demo (demo-ci runs
/// 43491427190, 43496633458, 43498525331, 43499354528), so hidden and already-excluded windows must be left alone.
/// </summary>
public sealed class CaptureExclusionPolicyTests
{
    private const uint None = 0x00;
    private const uint Monitor = 0x01;

    [Fact]
    public void A_visible_capturable_window_is_excluded_for_the_frame()
    {
        Assert.True(CaptureExclusionPolicy.NeedsTemporaryExclusion(isVisible: true, currentAffinity: None));
        Assert.True(CaptureExclusionPolicy.NeedsTemporaryExclusion(isVisible: true, currentAffinity: Monitor));
    }

    [Fact]
    public void A_hidden_window_is_never_touched()
    {
        Assert.False(CaptureExclusionPolicy.NeedsTemporaryExclusion(isVisible: false, currentAffinity: None));
        Assert.False(CaptureExclusionPolicy.NeedsTemporaryExclusion(isVisible: false, currentAffinity: Monitor));
        Assert.False(CaptureExclusionPolicy.NeedsTemporaryExclusion(isVisible: false, currentAffinity: CaptureExclusionPolicy.ExcludeFromCapture));
    }

    [Fact]
    public void An_already_excluded_window_needs_no_change()
    {
        Assert.False(CaptureExclusionPolicy.NeedsTemporaryExclusion(isVisible: true, currentAffinity: CaptureExclusionPolicy.ExcludeFromCapture));
    }

    [Fact]
    public void Exclusion_uses_the_Windows_exclude_from_capture_value()
    {
        Assert.Equal(0x11u, CaptureExclusionPolicy.ExcludeFromCapture);
    }
}
