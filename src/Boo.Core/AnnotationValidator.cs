namespace Boo.Core;

public static class AnnotationValidator
{
    private const double MaximumAgeSeconds = 15;

    public static ValidationResult Validate(AnnotationProposal proposal, ScreenContext current, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(current);

        if (proposal.ObservationId != current.ObservationId)
        {
            return Invalid("The annotation observation is no longer current.");
        }

        if (proposal.Generation != current.Generation)
        {
            return Invalid("The annotation generation is no longer current.");
        }

        if (!string.Equals(proposal.DisplayId, current.Display.Id, StringComparison.Ordinal))
        {
            return Invalid("The annotation display is no longer selected.");
        }

        if (!IsValidTarget(current.Target))
        {
            return Invalid("The current target identity is unavailable.");
        }

        var age = (now - current.CapturedAt).TotalSeconds;
        if (!double.IsFinite(age) || age < 0)
        {
            return Invalid("The annotation context was captured in the future.");
        }

        if (age > MaximumAgeSeconds)
        {
            return Invalid("The annotation context is stale.");
        }

        if (current.FrameWidth <= 0 || current.FrameHeight <= 0)
        {
            return Invalid("The captured frame has no usable dimensions.");
        }

        if (proposal.Marks is null || proposal.Marks.Count == 0)
        {
            return Invalid("The annotation proposal contains no marks.");
        }

        foreach (var mark in proposal.Marks)
        {
            if (mark is null)
            {
                return Invalid("The annotation proposal contains an empty mark.");
            }

            if (!IsSupportedKind(mark.Kind))
            {
                return Invalid("The annotation mark kind is not supported.");
            }

            if (!IsFinite(mark.Bounds) || mark.Bounds.Width <= 0 || mark.Bounds.Height <= 0)
            {
                return Invalid("The annotation mark bounds are invalid.");
            }

            if (!IsInFrame(mark.Bounds, current.FrameWidth, current.FrameHeight))
            {
                return Invalid("The annotation mark is outside the captured frame.");
            }

            if (!double.IsFinite(mark.EndX) || !double.IsFinite(mark.EndY) ||
                mark.EndX < 0 || mark.EndY < 0 ||
                mark.EndX > current.FrameWidth || mark.EndY > current.FrameHeight)
            {
                return Invalid("The annotation endpoint is outside the captured frame.");
            }
        }

        return new ValidationResult(true, string.Empty);
    }

    public static RectD MapToDisplay(RectD frameRect, ScreenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.FrameWidth <= 0 || context.FrameHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(context), "The captured frame must have positive dimensions.");
        }

        if (!IsFinite(frameRect))
        {
            throw new ArgumentException("The frame rectangle must contain finite values.", nameof(frameRect));
        }

        if (!double.IsFinite(context.Display.PixelBounds.X) || !double.IsFinite(context.Display.PixelBounds.Y) ||
            !double.IsFinite(context.Display.PixelBounds.Width) || !double.IsFinite(context.Display.PixelBounds.Height) ||
            context.Display.PixelBounds.Width <= 0 || context.Display.PixelBounds.Height <= 0)
        {
            throw new ArgumentException("The display pixel bounds must be finite and have positive dimensions.", nameof(context));
        }

        // Capture frames are already physical pixels. Use the frame-to-display
        // ratio so downsampled WGC frames do not apply DPI a second time.
        var scaleX = context.Display.PixelBounds.Width / context.FrameWidth;
        var scaleY = context.Display.PixelBounds.Height / context.FrameHeight;
        return new RectD(
            context.Display.PixelBounds.X + frameRect.X * scaleX,
            context.Display.PixelBounds.Y + frameRect.Y * scaleY,
            frameRect.Width * scaleX,
            frameRect.Height * scaleY);
    }

    private static ValidationResult Invalid(string reason) => new(false, reason);

    private static bool IsSupportedKind(string? kind) =>
        kind is "ring" or "arrow" or "label";

    private static bool IsValidTarget(WindowIdentity target) =>
        target.Handle != 0 &&
        target.ProcessId > 0 &&
        !string.IsNullOrWhiteSpace(target.ProcessName) &&
        IsFinite(target.Bounds) &&
        target.Bounds.Width > 0 &&
        target.Bounds.Height > 0;

    private static bool IsInFrame(RectD rect, int width, int height) =>
        rect.X >= 0 && rect.Y >= 0 &&
        rect.X + rect.Width <= width &&
        rect.Y + rect.Height <= height;

    private static bool IsFinite(RectD rect) =>
        double.IsFinite(rect.X) && double.IsFinite(rect.Y) &&
        double.IsFinite(rect.Width) && double.IsFinite(rect.Height);
}
public static class DisplayGeometry
{
    public static RectD NotchBounds(DisplayInfo display, double widthDip, double heightDip, double offsetDip = 0)
    {
        ArgumentNullException.ThrowIfNull(display);
        if (string.IsNullOrWhiteSpace(display.Id) ||
            !double.IsFinite(display.PixelBounds.X) || !double.IsFinite(display.PixelBounds.Y) ||
            !double.IsFinite(display.PixelBounds.Width) || !double.IsFinite(display.PixelBounds.Height) ||
            display.PixelBounds.Width <= 0 || display.PixelBounds.Height <= 0)
        {
            throw new ArgumentException("The display geometry must be finite and have positive dimensions.", nameof(display));
        }

        if (!double.IsFinite(display.Scale) || display.Scale <= 0)
        {
            throw new ArgumentException("The display scale must be positive and finite.", nameof(display));
        }

        if (!double.IsFinite(widthDip) || widthDip <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(widthDip), "The notch width must be positive and finite.");
        }

        if (!double.IsFinite(heightDip) || heightDip <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(heightDip), "The notch height must be positive and finite.");
        }

        if (!double.IsFinite(offsetDip))
        {
            throw new ArgumentOutOfRangeException(nameof(offsetDip), "The notch offset must be finite.");
        }

        var width = widthDip * display.Scale;
        var height = heightDip * display.Scale;
        return new RectD(
            display.PixelBounds.X + (display.PixelBounds.Width - width) / 2,
            display.PixelBounds.Y + offsetDip * display.Scale,
            width,
            height);
    }
}

