namespace Boo.Core;

/// <summary>Where the face parts sit inside the notch body (DIP, origin = body top-left).</summary>
public sealed record NotchFaceParts(PointD LeftEye, PointD RightEye, RectD IndicatorSlot);

/// <summary>
/// Pure placement of eyes and the state indicator for any face in the spec. The eyes keep the
/// position they have in the collapsed (idle) size; extra width (sideways growth) or extra height
/// (downward growth) becomes the indicator slot (thinking dots, record dot, pause bars, ...).
/// </summary>
public static class NotchFaceGeometry
{
    public static NotchFaceParts Place(NotchFaceSpec face, SizeD body)
    {
        ArgumentNullException.ThrowIfNull(face);
        var idle = face.SizeFor(NotchFaceState.Idle);
        var eye = face.Eye;
        var half = (eye.Width + eye.Gap) / 2;

        var extraWidth = Math.Max(0, body.Width - idle.Width);
        var extraHeight = Math.Max(0, body.Height - idle.Height);

        double cx, cy;
        RectD slot;
        if (extraWidth <= 0.5 && extraHeight <= 0.5)
        {
            cx = body.Width / 2;
            cy = body.Height / 2;
            slot = new RectD(body.Width, 0, 0, body.Height);
        }
        else if (face.Shape.Grow == NotchGrowDirection.Down || extraHeight > extraWidth)
        {
            // Grows down: eyes stay in the top band, indicator below them.
            cx = body.Width / 2;
            cy = idle.Height / 2;
            slot = new RectD(0, idle.Height, body.Width, extraHeight);
        }
        else
        {
            // Grows sideways: eyes keep a left "face" slot the size of the collapsed notch.
            cx = idle.Width / 2;
            cy = idle.Height / 2;
            slot = new RectD(idle.Width, 0, extraWidth, idle.Height);
        }

        return new NotchFaceParts(new PointD(cx - half, cy), new PointD(cx + half, cy), slot);
    }
}

public enum PressOutcome { None, Click, HoldEnded }

/// <summary>
/// Click versus press-and-hold on the notch eyes. A press becomes a hold once it has lasted
/// <see cref="HoldThreshold"/>; releasing before that is a click. Times come from an injected clock.
/// </summary>
public sealed class PressHoldTracker
{
    public static readonly TimeSpan HoldThreshold = TimeSpan.FromMilliseconds(200);

    private TimeSpan? _downAt;

    public bool IsPressed => _downAt is not null;
    public bool IsHeld { get; private set; }

    public void Down(TimeSpan now)
    {
        _downAt = now;
        IsHeld = false;
    }

    /// <summary>Returns true exactly once, when the press turns into a hold.</summary>
    public bool Tick(TimeSpan now)
    {
        if (_downAt is null || IsHeld || now - _downAt.Value < HoldThreshold) return false;
        IsHeld = true;
        return true;
    }

    /// <summary>Release. A press shorter than the threshold that never became a hold is a click.</summary>
    public PressOutcome Up(TimeSpan now)
    {
        if (_downAt is null) return PressOutcome.None;
        var wasHeld = IsHeld || now - _downAt.Value >= HoldThreshold;
        var hadHoldEvent = IsHeld;
        _downAt = null;
        IsHeld = false;
        if (hadHoldEvent) return PressOutcome.HoldEnded;
        return wasHeld ? PressOutcome.None : PressOutcome.Click;
    }

    /// <summary>Capture lost / cancelled. Returns true if a hold was active (listeners must get "released").</summary>
    public bool Cancel()
    {
        var held = IsHeld;
        _downAt = null;
        IsHeld = false;
        return held;
    }
}

/// <summary>
/// Studio row layout of the notch (docs/design/boo-notch-studio.html, ".boo .r"): a top row as tall as the collapsed
/// notch (spec hover height) holding [lead slot][eyes][trail slot] with 12 DIP side padding, 10 DIP gaps and
/// CSS justify-content: space-between; anything taller than the row is the caption area. DIP, origin = body top-left.
/// Lead = record dot while capturing; Trail = bars / dots / pause bars / stop square.
/// Caption = (16, row + 2, width - 32, height - row - 2), clamped to non-negative size.
/// </summary>
public sealed record NotchRowParts(PointD LeftEye, PointD RightEye, RectD Lead, RectD Trail, RectD Caption, double RowHeight);

public static class NotchRowLayout
{
    public const double Padding = 12;
    public const double Gap = 10;
    public const double CaptionInsetX = 16;
    public const double CaptionTop = 2;

    /// <summary>Lead/trail content widths for face B style sideways faces (studio slot contents):
    /// capture (7 record dot, 18 stop square); listening and responding (0, 25 = five 3-DIP bars, 2.5 gaps);
    /// thinking (0, 20 = three 4-DIP dots, 4 gaps); paused (0, 9 = two 3-DIP bars, 3 gap); everything else (0, 0).</summary>
    public static (double Lead, double Trail) SlotWidths(NotchFaceState state) => FaceMotionImpl.SlotWidths(state);

    /// <summary>
    /// Sideways faces: lead at x = 12; trail right-aligned at width - 12 - trail; eyes block (2·eye.w + eye.gap) placed by
    /// space-between: eyesX = 12 + lead + 10 + free/2 where free = width - 24 - lead - eyes - trail - 20; when free &lt; 0,
    /// items pack from the left (eyesX = 12 + lead + 10, trail right after the eyes + 10). Eye centres at y = row/2.
    /// Down-growing / centred faces (C): eyes centred on width, y = row/2; lead is empty at (12, 0, 0, row);
    /// trail is centred below the row: (width/2 - trail/2, row, trail, max(0, height - row - 6)).
    /// Lead/Trail rects span the full row height for sideways faces.
    /// </summary>
    public static NotchRowParts Place(NotchFaceSpec face, SizeD body, double leadWidth, double trailWidth)
        => FaceMotionImpl.PlaceRow(face, body, leadWidth, trailWidth);
}
