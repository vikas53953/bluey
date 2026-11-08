namespace Boo.Perf.Tests;

/// <summary>
/// One sample of the diagnostics file Boo rewrites every 500 ms when BOO_PERF_DIAG is set.
/// All counters are cumulative since process start.
/// </summary>
public sealed record DiagSnapshot(
    int Schema,
    DateTimeOffset WrittenAtUtc,
    long UptimeMs,
    long Gen0,
    long Gen1,
    long Gen2,
    long AllocatedBytes,
    long HeapBytes,
    long WorkingSetBytes,
    long NotchFrames,
    bool NotchRendering,
    string FaceState,
    long PacerSkipped);
