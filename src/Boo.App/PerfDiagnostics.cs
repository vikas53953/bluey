using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace Boo.App;

/// <summary>
/// Opt-in perf probe for the perf budget tests (tests/Boo.Perf.Tests) and hands-on profiling. When the
/// environment variable <c>BOO_PERF_DIAG</c> names a file, a low-priority timer rewrites that file every
/// <see cref="Interval"/> with a JSON snapshot of in-process counters nothing outside the process can read:
/// GC collections per generation, total allocated bytes, heap size, plus the notch render-loop state
/// (frames rendered, whether the CompositionTarget.Rendering subscription is live, face state, pacer skips) and how
/// Windows draws the notch ("SoftwareOnly" or "Default"; added 2026-11-12, readers that do not know it ignore it).
/// Unset (every normal launch) nothing is created: no timer, no file, no allocations.
/// The timer runs at <see cref="DispatcherPriority.Background"/>, not ApplicationIdle: while the notch is revealed the
/// render loop keeps the dispatcher busy and an ApplicationIdle tick can starve for seconds, which left the file stale
/// mid-measurement (CI 13 Nov 2026: 0 and 11 fps reported over a 2 s window while 222 and 216 frames had rendered).
/// Schema (version 1), all numbers cumulative since process start:
/// <code>
/// { "schema": 1, "writtenAtUtc": "...", "uptimeMs": 1234, "gen0": 3, "gen1": 1, "gen2": 0,
///   "allocatedBytes": 12345678, "heapBytes": 2345678, "workingSetBytes": 98765432,
///   "notchFrames": 0, "notchRendering": false, "faceState": "Hidden", "pacerSkipped": 0, "notchRenderMode": "SoftwareOnly" }
/// </code>
/// </summary>
internal sealed class PerfDiagnostics
{
    public const string EnvironmentVariable = "BOO_PERF_DIAG";
    public const int Schema = 1;
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _path;
    private readonly string _tempPath;
    private readonly NotchWindow _notch;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;

    private PerfDiagnostics(string path, NotchWindow notch, Dispatcher dispatcher)
    {
        _path = path;
        _tempPath = path + ".tmp";
        _notch = notch;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = Interval };
        _timer.Tick += (_, _) => Write();
    }

    /// <summary>Starts the probe when BOO_PERF_DIAG is set; returns null (and does nothing) otherwise.</summary>
    public static PerfDiagnostics? TryStart(NotchWindow notch, Dispatcher dispatcher)
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
            var probe = new PerfDiagnostics(full, notch, dispatcher);
            probe.Write();
            probe._timer.Start();
            return probe;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Trace.TraceWarning($"Boo perf diagnostics disabled: {ex.Message}");
            return null;
        }
    }

    public void Stop() => _timer.Stop();

    private void Write()
    {
        var snapshot = new Snapshot(
            Schema,
            DateTimeOffset.UtcNow,
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
            _notch.RenderModeName);
        try
        {
            File.WriteAllText(_tempPath, JsonSerializer.Serialize(snapshot, JsonOptions));
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

    private sealed record Snapshot(
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
        string NotchRenderMode);
}
