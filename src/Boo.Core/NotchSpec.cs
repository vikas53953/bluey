namespace Boo.Core;

// Face lane (U1/U2) contract. Data records are complete; behaviour stubs throw
// NotImplementedException until the builder implements them against the frozen tests
// in tests/Boo.Core.Tests/Face*.cs.

/// <summary>Logical (DIP) size. Multiply by the display scale for physical pixels.</summary>
public readonly record struct SizeD(double Width, double Height);

/// <summary>2-D point or offset (physical px unless stated otherwise).</summary>
public readonly record struct PointD(double X, double Y);

/// <summary>Every visual state of the notch face. Each state has exactly one size in the spec.</summary>
public enum NotchFaceState { Hidden, Hover, Idle, Listening, Thinking, Responding, Capture, Nudge, Paused, Happy }

public enum NotchGrowDirection { Sideways, Down }

public sealed record NotchShapeSpec(double BottomRadius, double Shoulder, NotchGrowDirection Grow, bool Centered);

/// <summary>Eye geometry in DIP. Pupil/Glint are 0 for faces that do not define them.</summary>
public sealed record NotchEyeSpec(string Kind, double Width, double Height, double Pupil, double Glint, double Gap, double GazeX, double GazeY);

public sealed record NotchFaceSpec(string Key, string Name, NotchShapeSpec Shape, NotchEyeSpec Eye, IReadOnlyDictionary<NotchFaceState, SizeD> Sizes)
{
    /// <summary>Spec size (DIP) for a state. Throws KeyNotFoundException if the state is missing.</summary>
    public SizeD SizeFor(NotchFaceState state) => throw new NotImplementedException();
}

/// <summary>All timings from spec "timings" (milliseconds unless named otherwise).</summary>
public sealed record NotchTimings(
    int RevealMs,
    int RetractDelayMs,
    int RetractMs,
    int ResizeMs,
    int BlinkCloseMs,
    int BlinkOpenMs,
    int BlinkEveryMinMs,
    int BlinkEveryMaxMs,
    double DoubleBlinkChance,
    double GazeStiffness,
    double GazeDamping,
    int CaptionCharMs);

public sealed class NotchSpecException : Exception
{
    public NotchSpecException(string message) : base(message) { }
    public NotchSpecException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Parsed docs/design/boo-notch-spec.json. Missing or invalid input:
/// <see cref="Load"/> throws <see cref="NotchSpecException"/>;
/// <see cref="LoadOrDefault"/> returns <see cref="Default"/> (values identical to face B of the shipped spec).
/// </summary>
public sealed class NotchSpec
{
    private NotchSpec() { }

    /// <summary>Built-in fallback: chosen face "B" only, hover strip 220x8, shipped timings.</summary>
    public static NotchSpec Default => throw new NotImplementedException();

    /// <summary>Parses the spec JSON. Throws NotchSpecException (never JsonException/NullReference) on any malformed, incomplete or inconsistent input.</summary>
    public static NotchSpec Load(Stream json) => throw new NotImplementedException();

    /// <summary>Load, or <see cref="Default"/> when the stream is null or the content is invalid.</summary>
    public static NotchSpec LoadOrDefault(Stream? json) => throw new NotImplementedException();

    /// <summary>Key of the face in use ("chosen.face" in the JSON).</summary>
    public string ChosenFaceKey => throw new NotImplementedException();

    /// <summary>The face in use (directions[ChosenFaceKey]).</summary>
    public NotchFaceSpec Face => throw new NotImplementedException();

    /// <summary>All face keys defined under "directions".</summary>
    public IReadOnlyCollection<string> FaceKeys => throw new NotImplementedException();

    /// <summary>Face by key (case-sensitive). Throws NotchSpecException for an unknown key.</summary>
    public NotchFaceSpec GetFace(string key) => throw new NotImplementedException();

    /// <summary>Copy of this spec with a different chosen face. Throws NotchSpecException for an unknown key.</summary>
    public NotchSpec WithChosenFace(string key) => throw new NotImplementedException();

    /// <summary>Invisible hover strip (DIP), spec "hoverStrip".</summary>
    public SizeD HoverStrip => throw new NotImplementedException();

    public NotchTimings Timings => throw new NotImplementedException();
}
