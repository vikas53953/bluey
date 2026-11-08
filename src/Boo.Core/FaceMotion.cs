namespace Boo.Core;

// Face polish contract (eyes + notch motion). Everything here is ported from the golden reference
// docs/design/boo-notch-studio.html (class Boo: mood(), tick(), CSS keyframes) and docs/design/boo-notch-spec.json.
// Units: time in ms (or TimeSpan), lengths in DIP (1 DIP = 1 px at 100% scaling).

/// <summary>
/// How the eyes look in one state (studio <c>Boo.mood()</c>).
/// Open = vertical eye scale before blinking; PupilScale = pupil size multiplier;
/// LookAt = fixed unit gaze target (each axis roughly -1..1) or null to follow the pointer / wander;
/// FollowPointer = track the pointer while it moves; Wander = glance around instead of following;
/// Blinks = blinking allowed; Happy = draw "^" arcs instead of round eyes; EyeOpacity = 0.72 when dimmed (paused).
/// </summary>
public readonly record struct FaceMood(
    double Open,
    double PupilScale,
    PointD? LookAt,
    bool FollowPointer,
    bool Wander,
    bool Blinks,
    bool Happy,
    double EyeOpacity);

/// <summary>Per-state moods. Values are exactly the studio's mood() table.</summary>
public static class FaceMoods
{
    /// <summary>
    /// listening: Open 1.14, PupilScale 1.15, follow.
    /// thinking: Open 0.74, LookAt (0.85 + 0.08·sin(nowMs/700), -0.9), PupilScale 0.9 (looks up and away).
    /// responding: Open 0.96 (the animator adds talkLevel·0.12), PupilScale 1.05, Wander (no follow).
    /// nudge: Open 1.2, LookAt (-0.7, 0.85), PupilScale 1.25.
    /// paused: Open 0.12, LookAt (0, 0.6), no blink, EyeOpacity 0.72 (eyes nearly closed, dimmed).
    /// happy: Open 1, LookAt (0, 0), no blink, Happy.
    /// hidden / hover / idle / capture: Open 1, PupilScale 1, follow.
    /// EyeOpacity is 1 and Blinks is true unless stated.
    /// </summary>
    public static FaceMood For(NotchFaceState state, double nowMs) => FaceMotionImpl.Mood(state, nowMs);
}

/// <summary>
/// One rendered face frame. Gaze = spring position in unit gaze space.
/// Button eyes (kind "button"): EyeShift = (Gaze.X·1, Gaze.Y·1 - Bounce) DIP (whole eye drifts 1 DIP),
/// PupilOffset = (Gaze.X·gazeX, Gaze.Y·gazeY) DIP inside the eye.
/// Other kinds (no pupil): EyeShift = (Gaze.X·gazeX, Gaze.Y·gazeY - Bounce), PupilOffset = (0,0).
/// Happy: EyeShift = (0, -Bounce), PupilOffset = (0,0).
/// Openness = max(0.06, mood.Open · blink) where blink runs 1 → 0.08 → 1 (studio tick()); never below 0.06.
/// Bounce (DIP, upward): responding TalkLevel·1.6; happy max(0, sin(nowMs/180))·1.5; else 0; always 0 in reduced motion.
/// TalkLevel in [0,1]: synthetic speech amplitude while responding, eases back to 0 otherwise.
/// </summary>
public readonly record struct FaceFrame(
    PointD Gaze,
    PointD EyeShift,
    PointD PupilOffset,
    double PupilScale,
    double Openness,
    double Bounce,
    double EyeOpacity,
    bool Happy,
    double TalkLevel);

/// <summary>Motion constants from the spec "timings" block that NotchTimings does not carry.</summary>
public static class FaceMotionTimings
{
    /// <summary>One blink this long after the face leaves Hidden (spec entryBlinkDelayMs).</summary>
    public const int EntryBlinkDelayMs = 120;
    /// <summary>Pointer still this long → eyes wander (spec idleWanderAfterMs).</summary>
    public const int IdleWanderAfterMs = 2000;
    /// <summary>New wander glance every 600..2200 ms (spec saccadeEveryMs).</summary>
    public const int SaccadeMinMs = 600;
    public const int SaccadeMaxMs = 2200;
    /// <summary>Gap between the two blinks of a double blink (studio: 60 ms).</summary>
    public const int DoubleBlinkGapMs = 60;
    /// <summary>Pointer distance (DIP, from the notch body centre) that maps to full gaze: x 320, y 220 (studio tick()).</summary>
    public const double FollowReachX = 320;
    public const double FollowReachY = 220;
}

/// <summary>
/// Drives the eyes frame by frame (studio <c>Boo.tick()</c>). Deterministic given the injected Random and clock.
/// - Gaze target: mood.LookAt if set; else, when FollowPointer and the pointer is known and moved (by more than 0.5 DIP)
///   within the last IdleWanderAfterMs, the pointer direction (dx/320, dy/220, each clamped to [-1,1]);
///   otherwise wander: a random glance x in [-0.9, 0.9], y in [-0.9, 0.4], replaced every 600..2200 ms.
///   Responding always wanders. Reduced motion: wander target is (0,0).
/// - Gaze follows the target with a spring (spec gazeSpring stiffness 260, damping ratio 0.82; reuse GazeSpring).
///   Reduced motion: gaze snaps to the target (no overshoot).
/// - Blinks: BlinkScheduler timeline started when the face leaves Hidden (DoubleBlinkGapMs 60), plus one entry blink
///   EntryBlinkDelayMs after leaving Hidden. Blink openness b in [0,1] maps to 0.08 + 0.92·b. Moods with Blinks=false
///   (paused, happy) do not blink. Blinks continue in reduced motion (spec: "blinks and state changes stay").
/// - TalkLevel: target = responding ? max(0, 0.5 + 0.5·sin(t/52)·sin(t/137)) : 0 (t = now ms);
///   level += (target - level)·min(1, dt_seconds·25).
/// </summary>
public sealed class FaceMotion
{
    private readonly FaceMotionImpl _impl;

    public FaceMotion(NotchSpec spec, Random random) => _impl = new FaceMotionImpl(spec, random);

    public bool ReducedMotion { get => _impl.ReducedMotion; set => _impl.ReducedMotion = value; }

    public NotchFaceState State => _impl.State;

    /// <summary>Change state. Leaving Hidden restarts the blink timeline and schedules the entry blink.</summary>
    public void SetState(NotchFaceState state, TimeSpan now) => _impl.SetState(state, now);

    /// <summary>
    /// Advance to <paramref name="now"/> (non-decreasing; the first call only initialises dt) and return the frame.
    /// <paramref name="pointer"/> = pointer position relative to the notch body centre in DIP, or null if unknown.
    /// </summary>
    public FaceFrame Tick(TimeSpan now, PointD? pointer) => _impl.Tick(now, pointer);
}

/// <summary>Small state indicators drawn next to the eyes (studio CSS + tick()).</summary>
public static class FaceIndicators
{
    /// <summary>Bars shown while listening and responding.</summary>
    public const int BarCount = 5;
    public const double BarWidth = 3;
    public const double BarGap = 2.5;
    /// <summary>Thinking dots: 3 dots, 4 DIP, 4 DIP apart.</summary>
    public const int DotCount = 3;
    public const double DotSize = 4;
    public const double DotGap = 4;

    /// <summary>Listening bar height: 3 + 12·(0.35 + 0.65·|sin(nowMs/(140 + 37·i) + i)|). Range [7.2, 15].</summary>
    public static double ListeningBarHeight(int index, double nowMs) => FaceMotionImpl.ListeningBar(index, nowMs);

    /// <summary>Talking bar height: 3 + 12·level·(0.4 + 0.6·|sin(nowMs/90 + 1.3·i)|). level 0 → 3.</summary>
    public static double TalkingBarHeight(int index, double nowMs, double level) => FaceMotionImpl.TalkingBar(index, nowMs, level);

    /// <summary>
    /// Thinking dot i, sinceMs after Thinking began. CSS keyframes over a 1200 ms cycle, delay 200·i ms:
    /// 0%,60%,100% → opacity 0.25, offsetY 0; 30% → opacity 1, offsetY -2. Each segment eased with CSS "ease"
    /// cubic-bezier(.25,.1,.25,1). Before its delay a dot rests at (0.25, 0). Reduced motion callers use (0.25, 0).
    /// </summary>
    public static (double Opacity, double OffsetY) ThinkingDot(int index, double sinceMs) => FaceMotionImpl.Dot(index, sinceMs);

    /// <summary>
    /// Capture dot halo (CSS box-shadow ring around the 7 DIP red dot), 2000 ms loop, ease-in-out (.42,0,.58,1):
    /// 0%/100% → spread 3 DIP, alpha 0.28; 50% → spread 1 DIP, alpha 0.10.
    /// </summary>
    public static (double Spread, double Alpha) RecordRing(double sinceMs) => FaceMotionImpl.Ring(sinceMs);

    /// <summary>
    /// Nudge shake X offset: two 320 ms iterations of keyframes 0% 0, 25% -3, 75% +3, 100% 0, each segment eased with
    /// cubic-bezier(.36,.07,.19,.97). 0 before 0 ms and from 640 ms on.
    /// </summary>
    public static double NudgeShakeX(double sinceMs) => FaceMotionImpl.Shake(sinceMs);
}
