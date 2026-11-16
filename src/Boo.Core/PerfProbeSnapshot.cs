using System.Text.Json;

namespace Boo.Core;

/// <summary>
/// The process counters the perf probe adds to each snapshot (issue #124): CPU split by user and kernel time, how much
/// the JIT has compiled, and the probe's own write cost. All cumulative since process start. Read from the runtime by
/// <c>Boo.Windows.RuntimeProbeCounters</c>; kept here as plain values so the mapping to JSON is testable.
/// </summary>
public readonly record struct PerfProbeCounters(
    TimeSpan UserCpu,
    TimeSpan KernelCpu,
    long JitMethods,
    TimeSpan JitTime,
    long ProbeWrites,
    TimeSpan ProbeWriteTime);

/// <summary>
/// Counts the probe's own writes and their total time. Only touched by the single writer the probe's guard lets
/// through, so plain fields are enough; readers see the writes finished before the current one.
/// </summary>
public sealed class ProbeWriteMeter
{
    public long Writes { get; private set; }

    public TimeSpan Total { get; private set; }

    public void Record(TimeSpan elapsed)
    {
        Writes++;
        Total += elapsed;
    }
}

/// <summary>
/// One BOO_PERF_DIAG snapshot (schema 1), written by Boo.App's PerfDiagnostics and read by tests/Boo.Perf.Tests and
/// scripts/perf. Field names and order are the file format: the perf test reads the first thirteen; the rest are
/// additive (renderMode 2026-11-12, the six process counters 2026-11-15), and readers ignore fields they do not know.
/// </summary>
public sealed record PerfProbeSnapshot(
    int Schema,
    DateTimeOffset WrittenAtUtc,
    long UptimeMs,
    int Gen0,
    int Gen1,
    int Gen2,
    long AllocatedBytes,
    long HeapBytes,
    long WorkingSetBytes,
    long NotchFrames,
    bool NotchRendering,
    string FaceState,
    long PacerSkipped,
    string NotchRenderMode,
    double UserCpuMs,
    double KernelCpuMs,
    long JitMethods,
    double JitMs,
    long ProbeWrites,
    double ProbeWriteMs)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The probe's process counters in milliseconds, each into its own field.</summary>
    public static PerfProbeSnapshot Create(
        int schema,
        DateTimeOffset writtenAtUtc,
        long uptimeMs,
        int gen0,
        int gen1,
        int gen2,
        long allocatedBytes,
        long heapBytes,
        long workingSetBytes,
        long notchFrames,
        bool notchRendering,
        string faceState,
        long pacerSkipped,
        string notchRenderMode,
        PerfProbeCounters counters) => new(
            schema, writtenAtUtc, uptimeMs, gen0, gen1, gen2, allocatedBytes, heapBytes, workingSetBytes,
            notchFrames, notchRendering, faceState, pacerSkipped, notchRenderMode,
            counters.UserCpu.TotalMilliseconds,
            counters.KernelCpu.TotalMilliseconds,
            counters.JitMethods,
            counters.JitTime.TotalMilliseconds,
            counters.ProbeWrites,
            counters.ProbeWriteTime.TotalMilliseconds);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}
