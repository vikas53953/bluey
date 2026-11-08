namespace Boo.Core;

/// <summary>
/// Whether Boo's own windows are excluded from screen capture (SetWindowDisplayAffinity with
/// WDA_EXCLUDEFROMCAPTURE = 0x11). Exclusion is ON unless the environment variable
/// BOO_QA_CAPTURABLE is exactly "1" (QA-only switch so native screenshots of Boo can be taken).
/// When exclusion is off, <see cref="Warning"/> is a visible, non-empty QA-mode warning that the
/// UI must show; when exclusion is on, Warning is null.
/// </summary>
public sealed class CaptureExclusionPolicy
{
    public const string QaEnvironmentVariable = "BOO_QA_CAPTURABLE";
    public const uint ExcludeFromCaptureAffinity = 0x11;
    public const uint NoAffinity = 0x0;

    /// <param name="readEnvironment">Reads an environment variable by name; returns null when unset.</param>
    public CaptureExclusionPolicy(Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(readEnvironment);
        ExcludeFromCapture = !string.Equals(readEnvironment(QaEnvironmentVariable), "1", StringComparison.Ordinal);
    }

    public bool ExcludeFromCapture { get; }

    /// <summary>The value to pass to SetWindowDisplayAffinity: 0x11 when excluding, 0 otherwise.</summary>
    public uint DisplayAffinity => ExcludeFromCapture ? ExcludeFromCaptureAffinity : NoAffinity;

    public string? Warning => ExcludeFromCapture
        ? null
        : "QA mode: Boo windows are capturable (BOO_QA_CAPTURABLE=1). Screenshots and screen shares will show Boo.";

    public static CaptureExclusionPolicy FromEnvironment() => new(Environment.GetEnvironmentVariable);
}
