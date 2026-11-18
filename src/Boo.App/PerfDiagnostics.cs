using System.Diagnostics;
using System.IO;
using System.Threading;
using Boo.Core;
using Boo.Windows;

namespace Boo.App;

/// <summary>
/// Opt-in perf probe for the perf budget tests (tests/Boo.Perf.Tests) and hands-on profiling. When the
/// environment variable <c>BOO_PERF_DIAG</c> names a file, a low-priority timer rewrites that file every
/// <see cref="Interval"/> with a JSON snapshot of in-process counters nothing outside the process can read:
/// GC collections per generation, total allocated bytes, heap size, plus the notch render-loop state
/// (frames rendered, whether the CompositionTarget.Rendering subscription is live, face state, pacer skips) and how
/// Windows draws the notch ("SoftwareOnly" or "Default"; added 2026-11-12, readers that do not know it ignore it).
/// Unset (every normal launch) nothing is created: no timer, no file, no allocations.
/// The writer is a <see cref="TimeProvider"/> timer on the thread pool, not a DispatcherTimer: while the notch is
/// revealed the render loop keeps the UI dispatcher busy and a dispatcher tick starves for seconds, so the file went
/// stale in the very window the perf test samples (CI 13 Nov 2026: 0, 11 and, at Background priority, 8 fps reported
/// over 2 s while 216 to 224 frames had rendered). Everything the snapshot reads is safe off the UI thread: the frame
/// count is Interlocked, the render mode is cached by the window when its handle is created, the rest are plain
/// fields and GC counters. A tick that finds the previous write still running is skipped.
/// Added 2026-11-15 for issue #124 (idle CPU), also additive: the process's own user and kernel CPU time, how many
/// methods the JIT has compiled and for how long (so a red idle-CPU reading can say whether it was compilation), and
/// how many probe writes there were and how long they took in total (so the probe's own cost is visible, not hidden
/// inside "idle"). All cumulative; the write counters cover the writes finished before this one. The counters are
/// read by <see cref="RuntimeProbeCounters"/> and mapped and serialized by <see cref="PerfProbeSnapshot"/> (both tested:
/// tests/Boo.Integration.Tests/RuntimeProbeCountersTests.cs, tests/Boo.Core.Tests/PerfProbeSnapshotTests.cs).
/// Schema (version 1), all numbers cumulative since process start:
/// <code>
/// { "schema": 1, "writtenAtUtc": "...", "uptimeMs": 1234, "gen0": 3, "gen1": 1, "gen2": 0,
///   "allocatedBytes": 12345678, "heapBytes": 2345678, "workingSetBytes": 98765432,
///   "notchFrames": 0, "notchRendering": false, "faceState": "Hidden", "pacerSkipped": 0, "notchRenderMode": "SoftwareOnly",
///   "userCpuMs": 812.5, "kernelCpuMs": 296.9, "jitMethods": 4210, "jitMs": 950.1, "probeWrites": 14, "probeWriteMs": 6.2 }
/// </code>
/// </summary>
internal sealed class PerfDiagnostics
{
    public const string EnvironmentVariable = "BOO_PERF_DIAG";
    public const int Schema = 1;
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    private readonly string _path;
    private readonly string _tempPath;
    private readonly NotchWindow _notch;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly TimeProvider _clock;
    private ITimer? _timer;
    private int _writing;
    private readonly ProbeWriteMeter _meter = new();

    private PerfDiagnostics(string path, NotchWindow notch, TimeProvider clock)
    {
        _clock = clock;
        _path = path;
        _tempPath = path + ".tmp";
        _notch = notch;
    }

    /// <summary>Starts the probe when BOO_PERF_DIAG is set; returns null (and does nothing) otherwise.</summary>
    public static PerfDiagnostics? TryStart(NotchWindow notch, TimeProvider? clock = null)
    {
        var path = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            var full = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var probe = new PerfDiagnostics(full, notch, clock ?? TimeProvider.System);
            probe.Write();
            probe._timer = probe._clock.CreateTimer(_ => probe.Write(), null, Interval, Interval);
            return probe;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Trace.TraceWarning($"Boo perf diagnostics disabled: {ex.Message}");
            return null;
        }
    }

    public void Stop() => _timer?.Dispose();

    private void Write()
    {
        if (Interlocked.Exchange(ref _writing, 1) != 0)
        {
            return;
        }
        var started = Stopwatch.GetTimestamp();
        try
        {
            WriteSnapshot();
        }
        finally
        {
            _meter.Record(Stopwatch.GetElapsedTime(started));
            Volatile.Write(ref _writing, 0);
        }
    }

    private void WriteSnapshot()
    {
        var snapshot = PerfProbeSnapshot.Create(
            Schema,
            _clock.GetUtcNow(),
            _uptime.ElapsedMilliseconds,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            GC.GetTotalAllocatedBytes(precise: false),
            GC.GetTotalMemory(forceFullCollection: false),
            Environment.WorkingSet,
            _notch.RenderedFrames,
            _notch.IsRenderLoopActive,
            _notch.FaceState.ToString(),
            _notch.PacerSkippedFrames,
            _notch.RenderModeName,
            RuntimeProbeCounters.Read(_meter));
        try
        {
            File.WriteAllText(_tempPath, snapshot.ToJson());
            File.Move(_tempPath, _path, overwrite: true);
        }
        catch (IOException)
        {
            // The reader may hold the file for a moment; the next tick rewrites it.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: transient share violation on Windows.
        }
    }
}
