namespace Boo.Windows;

/// <summary>
/// Which of Boo's own windows a screen capture must hide from itself for the length of one frame.
/// </summary>
/// <remarks>
/// Only a window that is on screen and not already excluded is switched to WDA_EXCLUDEFROMCAPTURE and back.
/// A hidden window is never in the frame, and changing the capture setting of a hidden window is not harmless:
/// on GitHub's Windows Server runners, switching the hidden (maximised) companion window to excluded and back on
/// each focus check left a blank white "Boo companion" frame on the real screen for about 20 s, with no visible
/// window behind it (demo-ci runs 43491427190, 43496633458, 43498525331, 43499354528; with hidden windows skipped,
/// run 43499554094 showed none). A window that is already excluded needs no change, so nothing is touched.
/// </remarks>
public static class CaptureExclusionPolicy
{
    /// <summary>SetWindowDisplayAffinity value that keeps a window out of every screen capture (Windows 10 2004+).</summary>
    public const uint ExcludeFromCapture = 0x11;

    /// <summary>True when this window must be excluded for the capture (and restored to <paramref name="currentAffinity"/> after).</summary>
    public static bool NeedsTemporaryExclusion(bool isVisible, uint currentAffinity) =>
        isVisible && currentAffinity != ExcludeFromCapture;
}
