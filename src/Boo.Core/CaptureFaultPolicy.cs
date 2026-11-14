namespace Boo.Core;

/// <summary>
/// QA-only switch that fails every per-request screen capture before a frame is taken, so the
/// no-observation path (a task reply that arrives with no frame and no control observation) can be
/// driven on the published app. Capture is faulted only when the environment variable
/// BOO_QA_CAPTURE_FAULT is exactly "1". The switch only removes a capability; it never widens one.
/// <see cref="Message"/> is the capture problem the panel reports while the switch is on.
/// </summary>
public sealed class CaptureFaultPolicy
{
    public const string QaEnvironmentVariable = "BOO_QA_CAPTURE_FAULT";

    /// <param name="readEnvironment">Reads an environment variable by name; returns null when unset.</param>
    public CaptureFaultPolicy(Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(readEnvironment);
        Faulted = string.Equals(readEnvironment(QaEnvironmentVariable), "1", StringComparison.Ordinal);
    }

    /// <summary>True when every request capture must fail without taking a frame.</summary>
    public bool Faulted { get; }

    public string Message => "QA mode: screen capture is faulted (BOO_QA_CAPTURE_FAULT=1). No frame was sent.";

    public static CaptureFaultPolicy FromEnvironment() => new(RuntimeEnvironment.Read);
}
