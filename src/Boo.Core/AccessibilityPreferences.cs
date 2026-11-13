namespace Boo.Core;

/// <summary>
/// Windows accessibility preferences that Boo honours, reduced to pure decisions so they can be tested
/// without a desktop. The app reads the live values (registry / SystemParameters) and feeds them here.
/// </summary>
public static class TextScale
{
    /// <summary>Windows "Text size" slider value range (Settings &gt; Accessibility &gt; Text size), in percent.</summary>
    public const int MinimumPercent = 100;
    public const int MaximumPercent = 225;

    /// <summary>
    /// Scale factor for a HKCU\Software\Microsoft\Accessibility\TextScaleFactor value. A missing, zero or
    /// out-of-range value becomes the nearest valid factor; nothing here can throw.
    /// </summary>
    public static double FromPercent(int? percent)
    {
        if (percent is not { } value)
        {
            return 1.0;
        }
        var clamped = Math.Clamp(value, MinimumPercent, MaximumPercent);
        return clamped / 100.0;
    }

    /// <summary>True when the factor changes layout enough to be worth applying (ignores float noise).</summary>
    public static bool IsScaled(double factor) => factor > 1.005;

    /// <summary>
    /// Minimum window size once the content is scaled: grows with the factor but never past the
    /// available work area, so a 225% user on a small screen still gets a window that fits.
    /// </summary>
    public static double ScaledMinimum(double baseMinimum, double factor, double available)
    {
        if (!double.IsFinite(baseMinimum) || baseMinimum <= 0)
        {
            return 0;
        }
        var safeFactor = double.IsFinite(factor) && factor > 0 ? factor : 1.0;
        var wanted = baseMinimum * safeFactor;
        if (!double.IsFinite(available) || available <= 0)
        {
            return wanted;
        }
        return Math.Min(wanted, available);
    }
}

/// <summary>Combines the Windows "Animation effects" switch with Boo's own Reduced motion setting.</summary>
public static class MotionPreference
{
    /// <param name="systemAnimationsEnabled">SystemParameters.ClientAreaAnimation.</param>
    /// <param name="appReducedMotion">Boo's Reduced motion checkbox.</param>
    public static bool ReducedMotion(bool systemAnimationsEnabled, bool appReducedMotion) =>
        !systemAnimationsEnabled || appReducedMotion;
}
