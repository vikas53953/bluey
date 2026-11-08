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
