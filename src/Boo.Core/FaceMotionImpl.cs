namespace Boo.Core;

internal sealed class FaceMotionImpl
{
    public FaceMotionImpl(NotchSpec spec, Random random) => throw new NotImplementedException();
    public bool ReducedMotion { get; set; }
    public NotchFaceState State { get; private set; }
    public void SetState(NotchFaceState state, TimeSpan now) => throw new NotImplementedException();
    public FaceFrame Tick(TimeSpan now, PointD? pointer) => throw new NotImplementedException();
    public static FaceMood Mood(NotchFaceState state, double nowMs) => throw new NotImplementedException();
    public static double ListeningBar(int i, double nowMs) => throw new NotImplementedException();
    public static double TalkingBar(int i, double nowMs, double level) => throw new NotImplementedException();
    public static (double, double) Dot(int i, double sinceMs) => throw new NotImplementedException();
    public static (double, double) Ring(double sinceMs) => throw new NotImplementedException();
    public static double Shake(double sinceMs) => throw new NotImplementedException();
    public static (double, double) SlotWidths(NotchFaceState state) => throw new NotImplementedException();
    public static NotchRowParts PlaceRow(NotchFaceSpec face, SizeD body, double lead, double trail) => throw new NotImplementedException();
}
