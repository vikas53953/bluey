namespace Boo.Core;

// Living ink (picked by Vikas 12 Nov 2026): the notch is a drop of wet ink with two cream eyes.
// Pure, testable maths for the look; NotchWindow only turns these numbers into WPF shapes.
// Source of the numbers: bluey-design-assets/living-ink (LIVING_INK_SPEC.md, v2/src/01-living-ink.html, living-ink.css).
// Units: DIP (1 DIP = 1 px at 100 %), milliseconds.

/// <summary>Living ink colours. The blot stays near-black and the eyes cream in BOTH light and dark Windows themes.</summary>
public static class LivingInkPalette
{
    /// <summary>Body of the pocket (blot), pupils and lids.</summary>
    public const string Ink = "#0C0A09";
    /// <summary>Eye fill, happy-squint stroke, speaking bars and caption text.</summary>
    public const string Cream = "#F7F4EE";
    /// <summary>Alert pupils (nudge / approve). Never the whole blot.</summary>
    public const string Alert = "#9B1B1B";
    /// <summary>Very quiet light rim (v1 token Boo.Color.NotchDarkRim) so the blot keeps an edge on a dark desktop.</summary>
    public const string DarkRim = "#2EFFFFFF";
    /// <summary>Eye shine is white at this opacity, upper-left of the eye, 28 % of the eye.</summary>
    public const double ShineOpacity = 0.85;

    /// <summary>The Living ink appearance (ink body, cream eyes).</summary>
    public static NotchAppearance Appearance { get; } = new(Ink, Cream);

    /// <summary>
    /// Colours to draw with. The untouched default appearance (#000000 / #FFFFFF, i.e. the user never picked colours)
    /// draws as Living ink; colours the user chose in Settings are kept as they are.
    /// </summary>
    public static NotchAppearance Resolve(NotchAppearance? appearance)
    {
        if (appearance is null) return Appearance;
        var isDefault = string.Equals(appearance.BodyColor, "#000000", StringComparison.OrdinalIgnoreCase)
            && string.Equals(appearance.EyeColor, "#FFFFFF", StringComparison.OrdinalIgnoreCase);
        return isDefault ? Appearance : appearance;
    }

    /// <summary>Theme decides only the rim: dark desktops get the quiet light rim, light desktops none. Body colour never changes.</summary>
    public static string? RimFor(bool darkTheme) => darkTheme ? DarkRim : null;
}

/// <summary>The face moods of Living ink (LIVING_INK_SPEC.md "Eye states").</summary>
public enum InkMood { Idle, Curious, Listening, Thinking, Speaking, Happy, Sleepy, Alert, Closed, Peek }

/// <summary>
/// Eye pose for one mood. ScaleX/ScaleY = squash of the whole eye about its centre; TopRadius = share of the eye height
/// taken by the upper curve (0.5 round, &gt;0.5 upper-heavy, &lt;0.5 lower-heavy); Pupil = pupil diameter as a share of
/// the eye width; PupilDx/Dy = fixed pupil offset as a share of the eye size; Lid = share of the eye covered by the lid
/// from the top (0 open, 1 shut); Alert = 1 for brick-red pupils; Happy = two cream arcs instead of eyes; Shine = shine opacity factor.
/// </summary>
public readonly record struct InkEyePose(
    double ScaleX,
    double ScaleY,
    double TopRadius,
    double Pupil,
    double PupilDx,
    double PupilDy,
    double Lid,
    double Alert,
    bool Happy,
    double Shine)
{
    public static InkEyePose For(InkMood mood) => mood switch
    {
        InkMood.Curious => new(1, 1.08, 0.56, 0.62, 0, 0, 0, 0, false, 1),
        InkMood.Listening => new(1.08, 1.08, 0.5, 0.70, 0, 0, 0, 0, false, 1),
        InkMood.Thinking => new(1, 1, 0.42, 0.48, 0, 0, 0.12, 0, false, 1),
        InkMood.Speaking => new(1, 1, 0.60, 0.46, 0, 0.08, 0, 0, false, 1),
        InkMood.Happy => new(1, 1, 0.5, 0, 0, 0, 0, 0, true, 0),
        InkMood.Sleepy => new(1, 0.72, 0.5, 0.46, 0, 0.14, 0.58, 0, false, 1),
        InkMood.Alert => new(1, 1.12, 0.40, 0.78, 0, 0, 0, 1, false, 1),
        InkMood.Closed => new(1, 1, 0.5, 0.45, 0, 0, 1, 0, false, 1),
        InkMood.Peek => new(1, 1, 0.5, 0.45, 0, 0, 0.70, 0, false, 1),
        _ => new(1, 1, 0.5, 0.45, 0, 0, 0, 0, false, 1),
    };

    /// <summary>Linear blend; Happy switches half way.</summary>
    public static InkEyePose Lerp(InkEyePose a, InkEyePose b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        static double L(double x, double y, double k) => x + (y - x) * k;
        return new InkEyePose(L(a.ScaleX, b.ScaleX, t), L(a.ScaleY, b.ScaleY, t), L(a.TopRadius, b.TopRadius, t),
            L(a.Pupil, b.Pupil, t), L(a.PupilDx, b.PupilDx, t), L(a.PupilDy, b.PupilDy, t), L(a.Lid, b.Lid, t),
            L(a.Alert, b.Alert, t), t >= 0.5 ? b.Happy : a.Happy, L(a.Shine, b.Shine, t));
    }

    /// <summary>How far (share of the eye) the pupil may travel from the centre for look-direction and still stay inside the eye.</summary>
    public double PupilTravel => Math.Max(0.04, (1 - Pupil) / 2 * 0.9);
}

/// <summary>
/// The ink blot outline: one closed figure, flush with the top edge, made of a top line and six cubic curves
/// (irregular lobed lower edge). Points are normalised to the unit box; the same 20 points exist in every mood so
/// moods morph point by point (never a swapped drawing).
/// </summary>
public sealed class InkBlot
{
    public const int PointCount = 20;
    private readonly double[] _p; // x0,y0, x1,y1, then 6 × (c1, c2, end)

    private InkBlot(double[] p) => _p = p;

    /// <summary>Canonical idle blot (viewBox 0 0 240 90).</summary>
    public static InkBlot Idle { get; } = Parse(240, 90, "12 0 228 0 236 0 238 8 236 18 232 50 214 78 176 82 150 86 132 70 120 72 100 76 86 88 50 80 20 72 8 44 6 18 4 8 5 0 12 0");
    /// <summary>Listening: wider, flatter (viewBox 280×90; the board's two middle curves merged into one so all moods share 6 curves).</summary>
    public static InkBlot Listening { get; } = Parse(280, 90, "14 0 266 0 276 0 278 10 276 22 272 50 258 70 230 78 210 84 190 70 170 72 130 92 50 70 24 72 8 62 4 36 4 18 4 6 6 0 14 0");
    /// <summary>Thinking: taller, deeper lobes (viewBox 200×110).</summary>
    public static InkBlot Thinking { get; } = Parse(200, 110, "16 0 184 0 194 0 196 10 194 24 190 60 170 88 130 96 110 100 96 86 88 86 64 86 44 104 28 94 10 82 6 50 6 24 6 8 8 0 16 0");
    /// <summary>Speaking: wide (viewBox 340×90).</summary>
    public static InkBlot Speaking { get; } = Parse(340, 90, "12 0 328 0 338 0 340 8 338 20 334 52 316 76 270 82 230 88 200 70 180 72 150 76 130 88 90 82 40 74 16 50 8 20 6 8 6 0 12 0");
    /// <summary>Happy: squashed (viewBox 240×80).</summary>
    public static InkBlot Happy { get; } = Parse(240, 80, "16 0 224 0 234 0 236 8 234 16 228 44 210 62 170 70 140 76 124 60 118 60 96 60 80 76 48 68 20 60 8 36 6 16 5 6 8 0 16 0");
    /// <summary>Sleepy / paused: slightly shorter (viewBox 240×90).</summary>
    public static InkBlot Sleepy { get; } = Parse(240, 90, "16 0 224 0 232 0 234 8 232 16 226 40 214 62 174 68 148 72 130 60 120 60 100 60 84 74 50 68 22 62 10 36 8 16 6 6 8 0 16 0");
    /// <summary>Closed (wake-up "eyes shut", viewBox 240×80).</summary>
    public static InkBlot Closed { get; } = Parse(240, 80, "20 0 220 0 232 0 234 8 232 16 226 40 210 58 170 64 140 68 124 54 118 54 96 54 80 70 48 62 22 56 10 32 8 16 7 6 10 0 20 0");
    /// <summary>Flat smear (wake-up stain and blink frame, viewBox 240×70).</summary>
    public static InkBlot Flat { get; } = Parse(240, 70, "16 0 224 0 234 0 236 8 234 14 228 34 214 50 174 56 144 60 128 48 118 48 96 48 80 62 48 56 22 50 10 28 8 14 7 6 10 0 16 0");
    /// <summary>Bead (wake-up): the stain gathered into a small round drop (viewBox 36×28, border-radius 0 0 18 18).</summary>
    public static InkBlot Bead { get; } = Parse(36, 28, "2 0 34 0 36 0 36 4 36 10 36 20 30 28 22 28 20 28 19 28 18 28 17 28 16 28 14 28 6 28 0 20 0 10 0 4 0 0 2 0");
    /// <summary>Looking down (hero board, viewBox 240×90): used while Boo watches the screen (capture).</summary>
    public static InkBlot LookDown { get; } = Parse(240, 90, "10 0 230 0 238 0 240 8 238 20 234 50 216 76 176 84 148 90 128 70 118 72 98 76 84 90 48 82 18 74 8 46 5 20 3 8 4 0 10 0");

    /// <summary>Normalised point i (0..19).</summary>
    public PointD this[int i] => new(_p[2 * i], _p[2 * i + 1]);

    public static InkBlot Lerp(InkBlot a, InkBlot b, double t)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (t <= 0) return a;
        if (t >= 1) return b;
        var p = new double[2 * PointCount];
        for (var i = 0; i < p.Length; i++) p[i] = a._p[i] + (b._p[i] - a._p[i]) * t;
        return new InkBlot(p);
    }

    /// <summary>
    /// Maps a normalised point into a body box. Up to 260×64 DIP this is a plain stretch (the board's
    /// preserveAspectRatio="none"); larger boxes (captioned states) keep the lobes and corners at the size of a
    /// 260×64 blot instead of ballooning them, so the caption area stays inside the ink.
    /// </summary>
    public static PointD Map(PointD n, SizeD box)
    {
        var w = box.Width;
        var h = box.Height;
        var refW = Math.Min(w, 260);
        double x;
        if (n.X <= 0.3) x = n.X * refW;
        else if (n.X >= 0.7) x = w - (1 - n.X) * refW;
        else x = 0.3 * refW + (n.X - 0.3) / 0.4 * (w - 0.6 * refW);

        var refH = Math.Min(h, 64);
        var y = n.Y <= 0.5 ? n.Y / 0.5 * (h - 0.5 * refH) : h - 0.5 * refH + (n.Y - 0.5) * refH;
        return new PointD(x, y);
    }

    /// <summary>The 20 outline points mapped into <paramref name="box"/> (start, top-right, then 6 × (c1, c2, end)).</summary>
    public PointD[] Points(SizeD box)
    {
        var result = new PointD[PointCount];
        for (var i = 0; i < PointCount; i++) result[i] = Map(this[i], box);
        return result;
    }

    /// <summary>The outline flattened to a polygon (each curve sampled <paramref name="perCurve"/> times), for hit and safe-area checks.</summary>
    public IReadOnlyList<PointD> Outline(SizeD box, int perCurve = 24)
    {
        var p = Points(box);
        var poly = new List<PointD> { p[0], p[1] };
        var from = p[1];
        for (var c = 0; c < 6; c++)
        {
            var c1 = p[2 + 3 * c];
            var c2 = p[3 + 3 * c];
            var to = p[4 + 3 * c];
            for (var k = 1; k <= perCurve; k++)
            {
                var t = k / (double)perCurve;
                var u = 1 - t;
                poly.Add(new PointD(
                    u * u * u * from.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * to.X,
                    u * u * u * from.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * to.Y));
            }
            from = to;
        }
        return poly;
    }

    /// <summary>True when <paramref name="point"/> (DIP in the body box) is inside the ink.</summary>
    public bool Contains(PointD point, SizeD box)
    {
        var poly = Outline(box);
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            if ((a.Y > point.Y) != (b.Y > point.Y)
                && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    private static InkBlot Parse(double vw, double vh, string numbers)
    {
        var v = numbers.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (v.Length != 2 * PointCount) throw new InvalidOperationException("Ink blot needs 20 points.");
        for (var i = 0; i < v.Length; i += 2)
        {
            v[i] /= vw;
            v[i + 1] /= vh;
        }
        return new InkBlot(v);
    }
}

/// <summary>A small ink shape in normalised body-box coordinates (centre, radii) or DIP radius.</summary>
public readonly record struct InkSpot(double X, double Y, double RadiusX, double RadiusY);

/// <summary>State → mood/blot/extras, layout and indicator maths for Living ink.</summary>
public static class LivingInk
{
    /// <summary>Speaking bars: four cream bars, 3 DIP wide, 3 DIP apart, resting heights 7/16/11/18.</summary>
    public const int BarCount = 4;
    public const double BarWidth = 3;
    public const double BarGap = 3;
    private static readonly double[] BarRest = { 7, 16, 11, 18 };

    /// <summary>Thinking drip: an 8×16 rounded drop at 62 % of the width, hanging from the lower edge.</summary>
    public const double DripWidth = 8;
    public const double DripHeight = 16;
    public const double DripX = 0.62;

    /// <summary>Rising ink dots while thinking (diameters, smallest first).</summary>
    public static IReadOnlyList<double> ThinkDotSizes { get; } = new[] { 5d, 7, 10 };

    /// <summary>Listening side blooms (normalised in the listening box, from the board svg).</summary>
    public static IReadOnlyList<InkSpot> Blooms { get; } = new[]
    {
        new InkSpot(16 / 280d, 44 / 90d, 10 / 280d, 7 / 90d),
        new InkSpot(266 / 280d, 46 / 90d, 11 / 280d, 8 / 90d),
    };

    /// <summary>Focus-done splatters (normalised centre in the happy box, radius in DIP).</summary>
    public static IReadOnlyList<InkSpot> Splatters { get; } = new[]
    {
        new InkSpot(18 / 240d, 74 / 80d, 3.8, 3.8),
        new InkSpot(228 / 240d, 68 / 80d, 3.2, 3.2),
        new InkSpot(210 / 240d, 80 / 80d, 2, 2),
        new InkSpot(36 / 240d, 80 / 80d, 2.3, 2.3),
    };

    public static InkMood MoodFor(NotchFaceState state) => state switch
    {
        NotchFaceState.Hover => InkMood.Curious,
        NotchFaceState.Capture => InkMood.Curious,
        NotchFaceState.Listening => InkMood.Listening,
        NotchFaceState.Thinking => InkMood.Thinking,
        NotchFaceState.Responding => InkMood.Speaking,
        NotchFaceState.Nudge => InkMood.Alert,
        NotchFaceState.Paused => InkMood.Sleepy,
        NotchFaceState.Happy => InkMood.Happy,
        _ => InkMood.Idle,
    };

    public static InkBlot BlotFor(NotchFaceState state) => state switch
    {
        NotchFaceState.Listening => InkBlot.Listening,
        NotchFaceState.Thinking => InkBlot.Thinking,
        NotchFaceState.Responding => InkBlot.Speaking,
        NotchFaceState.Paused => InkBlot.Sleepy,
        NotchFaceState.Happy => InkBlot.Happy,
        NotchFaceState.Capture => InkBlot.LookDown,
        _ => InkBlot.Idle,
    };

    public static bool HasBlooms(NotchFaceState state) => state == NotchFaceState.Listening;
    public static bool HasDrip(NotchFaceState state) => state == NotchFaceState.Thinking;
    public static bool HasThinkDots(NotchFaceState state) => state == NotchFaceState.Thinking;
    public static bool HasBars(NotchFaceState state) => state == NotchFaceState.Responding;

    /// <summary>Morph length into a state: listen ~220 ms, think slower (360 ms), speak 200 ms, others 240 ms.</summary>
    public static int MorphMs(NotchFaceState to) => to switch
    {
        NotchFaceState.Listening => 220,
        NotchFaceState.Thinking => 360,
        NotchFaceState.Responding => 200,
        _ => 240,
    };

    /// <summary>Lead/trail slot widths: capture = record dot + stop square, responding = the four bars, else none.</summary>
    public static (double Lead, double Trail) SlotWidths(NotchFaceState state) => state switch
    {
        NotchFaceState.Capture => (7, 18),
        NotchFaceState.Responding => (0, BarCount * BarWidth + (BarCount - 1) * BarGap),
        _ => (0, 0),
    };

    public const double SidePadding = 18;
    public const double CaptionInsetX = 30;

    /// <summary>
    /// Living ink placement inside the body box: the eyes are centred in the top row (row = collapsed height), or centred
    /// between the lead and trail slots when those are used; the caption sits under the row and ends above the lobed
    /// lower band (0.42 × min(height, 64)) so text never runs off the ink. Eyes are lifted when the mood's blot is
    /// shallow under them (sleepy, happy), so an eye never hangs over the blot's lower edge.
    /// </summary>
    public static NotchRowParts Place(NotchFaceSpec face, SizeD body, NotchFaceState state)
    {
        ArgumentNullException.ThrowIfNull(face);
        var (lead, trail) = SlotWidths(state);
        var row = Math.Min(face.SizeFor(NotchFaceState.Hover).Height, body.Height);
        var eye = face.Eye;
        var half = (eye.Width + eye.Gap) / 2;
        var cy = Math.Min(row / 2, BottomUnder(BlotFor(state), body, 0.5) - eye.Height * 0.36 - 2);
        cy = Math.Max(cy, eye.Height * 0.4);
        double cx;
        if (lead <= 0 && trail <= 0)
        {
            cx = body.Width / 2;
        }
        else
        {
            var left = SidePadding + (lead > 0 ? lead + 10 : 0);
            var right = body.Width - SidePadding - (trail > 0 ? trail + 10 : 0);
            cx = (left + right) / 2;
        }
        var band = 0.42 * Math.Min(body.Height, 64);
        var caption = new RectD(CaptionInsetX, row + 2,
            Math.Max(0, body.Width - 2 * CaptionInsetX), Math.Max(0, body.Height - row - 2 - band));
        return new NotchRowParts(
            new PointD(cx - half, cy),
            new PointD(cx + half, cy),
            new RectD(SidePadding, 0, lead, row),
            new RectD(body.Width - SidePadding - trail, 0, trail, row),
            caption, row);
    }

    /// <summary>Y (DIP) of the blot's lower edge at <paramref name="xShare"/> of the width: the shallowest point within ±10 %.</summary>
    public static double BottomUnder(InkBlot blot, SizeD body, double xShare)
    {
        ArgumentNullException.ThrowIfNull(blot);
        var x = body.Width * xShare;
        var band = body.Width * 0.1;
        var bottom = body.Height;
        foreach (var p in blot.Outline(body, 12))
        {
            if (p.Y > body.Height * 0.3 && Math.Abs(p.X - x) <= band && p.Y < bottom)
            {
                bottom = p.Y;
            }
        }
        return bottom;
    }

    /// <summary>
    /// Speaking bar height. Keeps time with the talk level (0..1): level 0 = 4 DIP stubs, full level swings up to the
    /// resting height. Reduced motion: the resting heights, still.
    /// </summary>
    public static double BarHeight(int index, double nowMs, double level, bool reducedMotion)
    {
        var rest = BarRest[Math.Clamp(index, 0, BarCount - 1)];
        if (reducedMotion) return rest;
        var swing = 0.35 + 0.65 * Math.Abs(Math.Sin(nowMs / 90 + 1.3 * index));
        return 4 + (rest - 4) * Math.Clamp(level, 0, 1) * swing;
    }
}

/// <summary>One drawn pose of the body and eyes.</summary>
public readonly record struct InkMorphFrame(InkBlot Blot, InkEyePose Pose, SizeD Size, double Progress)
{
    public bool Settled => Progress >= 1;
}

/// <summary>
/// Cancellable morph between moods. Retarget starts from the pose drawn at that instant (no jump, nothing queued);
/// reduced motion or a snap jumps straight to the end pose. Ease-out cubic.
/// </summary>
public sealed class InkMorph
{
    private InkBlot _fromBlot = InkBlot.Idle, _toBlot = InkBlot.Idle;
    private InkEyePose _fromPose = InkEyePose.For(InkMood.Idle), _toPose = InkEyePose.For(InkMood.Idle);
    private SizeD _fromSize, _toSize;
    private TimeSpan _start;
    private double _durationMs = 1;

    public InkMorph(SizeD size) => _fromSize = _toSize = size;

    public SizeD TargetSize => _toSize;

    public void Retarget(InkBlot blot, InkEyePose pose, SizeD size, TimeSpan now, double durationMs, bool snap)
    {
        ArgumentNullException.ThrowIfNull(blot);
        var current = Sample(now);
        if (snap || durationMs <= 0)
        {
            _fromBlot = blot; _fromPose = pose; _fromSize = size;
        }
        else
        {
            _fromBlot = current.Blot; _fromPose = current.Pose; _fromSize = current.Size;
        }
        _toBlot = blot; _toPose = pose; _toSize = size;
        _durationMs = Math.Max(1, durationMs);
        _start = snap ? now - TimeSpan.FromMilliseconds(_durationMs) : now; // a snap is already at its end pose
    }

    public InkMorphFrame Sample(TimeSpan now)
    {
        var t = Math.Clamp((now - _start).TotalMilliseconds / _durationMs, 0, 1);
        if (t >= 1) return new InkMorphFrame(_toBlot, _toPose, _toSize, 1);
        var k = 1 - Math.Pow(1 - t, 3);
        return new InkMorphFrame(
            InkBlot.Lerp(_fromBlot, _toBlot, k),
            InkEyePose.Lerp(_fromPose, _toPose, k),
            new SizeD(_fromSize.Width + (_toSize.Width - _fromSize.Width) * k, _fromSize.Height + (_toSize.Height - _fromSize.Height) * k),
            t);
    }
}

public enum InkWakePhase { Stain, Bead, EyesShut, Peek, Blink, LooksOver, Done }

/// <summary>One wake-up frame: blot shape and size, body opacity, whether the eyes are drawn, lid share and eye squash.</summary>
public readonly record struct InkWakeFrame(InkWakePhase Phase, InkBlot Blot, SizeD Size, double Opacity, bool EyesVisible, double Lid, double EyeScaleY);

/// <summary>
/// Wake-up (~520 ms, cancellable): stain (thin 72×5 mark at 28 %) → bead (36×28 drop) → eyes shut (pocket forms,
/// lids closed) → peek (lids ~70 %) → hard blink (thin cream lines, 6/32 high) → looks over (lids open, curious squash).
/// The "stain becomes a pocket" beat is 180 ms. Reduced motion never starts it.
/// </summary>
public sealed class InkWakeUp
{
    public const int TotalMs = 520;
    public const int StainToPocketMs = 180;

    private readonly record struct Key(double Ms, InkWakePhase Phase, InkBlot Blot, SizeD Size, double Opacity, bool Eyes, double Lid, double EyeScaleY);

    private readonly Key[] _keys;
    private readonly SizeD _rest;
    private TimeSpan _start;

    public InkWakeUp(SizeD restSize)
    {
        _rest = restSize;
        var shut = new SizeD(restSize.Width * 148 / 156, restSize.Height * 44 / 58);
        _keys = new[]
        {
            new Key(0, InkWakePhase.Stain, InkBlot.Flat, new SizeD(72, 5), 0.28, false, 1, 1),
            new Key(90, InkWakePhase.Bead, InkBlot.Bead, new SizeD(72, 5), 0.28, false, 1, 1),
            new Key(StainToPocketMs, InkWakePhase.EyesShut, InkBlot.Bead, new SizeD(36, 28), 1, false, 1, 1),
            new Key(250, InkWakePhase.Peek, InkBlot.Closed, shut, 1, true, 1, 1),
            new Key(330, InkWakePhase.Blink, InkBlot.Idle, restSize, 1, true, 0.70, 1),
            new Key(420, InkWakePhase.LooksOver, InkBlot.Idle, restSize, 1, true, 0, 6 / 32d),
            new Key(TotalMs, InkWakePhase.Done, InkBlot.Idle, restSize, 1, true, 0, 1.08),
        };
    }

    public bool Active { get; private set; }

    /// <summary>Starts the sequence; with reduced motion nothing plays (Active stays false).</summary>
    public void Start(TimeSpan now, bool reducedMotion)
    {
        _start = now;
        Active = !reducedMotion;
    }

    /// <summary>A new state cuts the wake-up at once.</summary>
    public void Cancel() => Active = false;

    public InkWakeFrame At(TimeSpan now)
    {
        var ms = (now - _start).TotalMilliseconds;
        if (!Active || ms >= TotalMs)
        {
            Active = false;
            return new InkWakeFrame(InkWakePhase.Done, InkBlot.Idle, _rest, 1, true, 0, 1);
        }
        if (ms < 0) ms = 0;
        var i = 0;
        while (i < _keys.Length - 2 && ms >= _keys[i + 1].Ms) i++;
        var a = _keys[i];
        var b = _keys[i + 1];
        var t = (ms - a.Ms) / (b.Ms - a.Ms);
        var k = 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 2);
        static double L(double x, double y, double q) => x + (y - x) * q;
        return new InkWakeFrame(
            PhaseAt(ms),
            InkBlot.Lerp(a.Blot, b.Blot, k),
            new SizeD(L(a.Size.Width, b.Size.Width, k), L(a.Size.Height, b.Size.Height, k)),
            L(a.Opacity, b.Opacity, k),
            a.Eyes,
            L(a.Lid, b.Lid, k),
            L(a.EyeScaleY, b.EyeScaleY, k));
    }

    private static InkWakePhase PhaseAt(double ms) => ms switch
    {
        < 90 => InkWakePhase.Stain,
        < StainToPocketMs => InkWakePhase.Bead,
        < 250 => InkWakePhase.EyesShut,
        < 330 => InkWakePhase.Peek,
        < 420 => InkWakePhase.Blink,
        < TotalMs => InkWakePhase.LooksOver,
        _ => InkWakePhase.Done,
    };
}

/// <summary>Celebrate frame: body squash factors and splatter scale/opacity.</summary>
public readonly record struct InkCelebrateFrame(double SquashX, double SquashY, double SplatterScale, double SplatterOpacity, bool Done);

/// <summary>
/// Focus session complete: ~1 s squash + smile arcs + splatters that fade, then it settles (squash 1, no splatters).
/// Reduced motion never starts it (end pose at once).
/// </summary>
public sealed class InkCelebrate
{
    public const int DurationMs = 1000;
    private TimeSpan _start;

    public bool Active { get; private set; }

    public void Start(TimeSpan now, bool reducedMotion)
    {
        _start = now;
        Active = !reducedMotion;
    }

    public void Cancel() => Active = false;

    public InkCelebrateFrame At(TimeSpan now)
    {
        var ms = (now - _start).TotalMilliseconds;
        if (!Active || ms >= DurationMs)
        {
            Active = false;
            return new InkCelebrateFrame(1, 1, 0, 0, true);
        }
        ms = Math.Max(0, ms);
        static double Out(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);
        double sx, sy;
        if (ms < 200) { var k = Out(ms / 200); sx = 1 + 0.10 * k; sy = 1 - 0.14 * k; }
        else if (ms < 450) { var k = Out((ms - 200) / 250); sx = 1.10 - 0.13 * k; sy = 0.86 + 0.19 * k; }
        else if (ms < 700) { var k = Out((ms - 450) / 250); sx = 0.97 + 0.03 * k; sy = 1.05 - 0.05 * k; }
        else { sx = 1; sy = 1; }
        var scale = Out(ms / 160);
        var opacity = ms < 450 ? 1 : 1 - Out((ms - 450) / (DurationMs - 450));
        return new InkCelebrateFrame(sx, sy, scale, Math.Max(0, opacity), false);
    }
}

/// <summary>
/// Soft lid blink: every 4–6 s, lid down 80 ms, hold 60 ms, up 80 ms (both lids, no body squash). Sleepy blinks are
/// slower (×1.75) and slightly uneven (each blink ×0.85–1.15). Reduced motion: no lid animation. Deterministic per Random.
/// Times must be non-decreasing between Restart calls.
/// </summary>
public sealed class InkBlink
{
    public const int DownMs = 80;
    public const int HoldMs = 60;
    public const int UpMs = 80;
    public const int EveryMinMs = 4000;
    public const int EveryMaxMs = 6000;
    public const double SleepyFactor = 1.75;

    private readonly Random _random;
    private TimeSpan _current = TimeSpan.MinValue;
    private double _uneven = 1;
    private TimeSpan _next;

    public InkBlink(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        _random = random;
    }

    /// <summary>Start a fresh timeline: first blink 4–6 s after <paramref name="now"/>.</summary>
    public void Restart(TimeSpan now)
    {
        _current = TimeSpan.MinValue;
        _next = now + Interval();
    }

    /// <summary>Start time of the next scheduled blink.</summary>
    public TimeSpan NextBlink => _next;

    /// <summary>Lid share (0 open .. 1 shut) from blinking at <paramref name="now"/>.</summary>
    public double LidAt(TimeSpan now, bool sleepy, bool reducedMotion)
    {
        while (now >= _next)
        {
            _current = _next;
            _uneven = 0.85 + 0.30 * _random.NextDouble();
            _next += Interval();
        }
        if (reducedMotion || _current == TimeSpan.MinValue) return 0;
        var f = sleepy ? SleepyFactor * _uneven : 1;
        var ms = (now - _current).TotalMilliseconds;
        double down = DownMs * f, hold = HoldMs * f, up = UpMs * f;
        if (ms < 0 || ms >= down + hold + up) return 0;
        if (ms < down) return ms / down;
        if (ms < down + hold) return 1;
        return 1 - (ms - down - hold) / up;
    }

    private TimeSpan Interval() => TimeSpan.FromMilliseconds(EveryMinMs + _random.NextDouble() * (EveryMaxMs - EveryMinMs));
}
