using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using Xunit.Abstractions;

namespace Boo.Perf.Tests;

/// <summary>
/// One sequential run against the published Boo.exe that measures startup, idle CPU / memory / allocation
/// rate and the notch render loop (off while hidden, on while revealed, off again after retract),
/// records every number to perf-results.json/.md, and then fails once listing every budget breach.
/// </summary>
[Collection("Boo perf (serial)")]
public sealed class BooPerfBudgetTests : IClassFixture<PerfAppFixture>
{
    /// <summary>Probe-clock seconds the revealed render loop is sampled for before frames per second is judged.</summary>
    private const int RevealSampleSeconds = 3;

    /// <summary>Longest wall-clock wait for that sample; a probe that never advances fails the fps budget loudly.</summary>
    private static readonly TimeSpan RevealSampleTimeout = TimeSpan.FromSeconds(10);

    private const string Hidden = "Hidden";

    private readonly PerfAppFixture _app;
    private readonly ITestOutputHelper _output;
    private readonly List<string> _failures = [];

    public BooPerfBudgetTests(PerfAppFixture app, ITestOutputHelper output)
    {
        _app = app;
        _output = output;
    }

    [Fact]
    public void Idle_startup_memory_cpu_and_render_loop_stay_within_budget()
    {
        var budgets = _app.Budgets;
        Log($"budgets: {PerfBudgets.ResolvePath()}");
        Log($"exe: {_app.ExePath}");
        Log($"diag: {_app.DiagPath}");

        // ---- a. startup → notch window visible via UIA, and first diag write ----
        Window? notch = null;
        var startupTimeout = TimeSpan.FromSeconds(60);
        while (_app.StartClock.Elapsed < startupTimeout)
        {
            notch = _app.FindNotch();
            if (notch is not null)
            {
                break;
            }
            if (_app.Process.HasExited)
            {
                break;
            }
            Thread.Sleep(50);
        }
        var startupToNotchMs = _app.StartClock.Elapsed.TotalMilliseconds;

        if (notch is null)
        {
            _app.CaptureScreen("startup-no-notch");
            var exited = _app.Process.HasExited ? $" Boo.exe exited with code {_app.Process.ExitCode}." : string.Empty;
            throw new TimeoutException(
                $"Notch window (UIA name '{PerfAppFixture.NotchName}' or title '{PerfAppFixture.NotchTitle}') not found " +
                $"within {startupTimeout.TotalSeconds:0}s after launching Boo.exe.{exited}");
        }

        if (!PerfAppFixture.WaitUntil(_app.DiagExists, TimeSpan.FromSeconds(30), pollMs: 100))
        {
            _app.CaptureScreen("startup-no-diag");
            throw new TimeoutException(
                $"Boo never wrote the BOO_PERF_DIAG file '{_app.DiagPath}' within 30s of launch " +
                "(the notch appeared, so the app is running but is not honouring BOO_PERF_DIAG).");
        }
        var firstDiag = _app.ReadDiag();
        Log($"first diag: schema={firstDiag.Schema} uptimeMs={firstDiag.UptimeMs} faceState={firstDiag.FaceState} notchRendering={firstDiag.NotchRendering}");

        Record("startupToNotchMs", Math.Round(startupToNotchMs), budgets.StartupToNotchMs, Compare.AtMost);
        Record("diagFirstWriteMs", Math.Round(_app.StartClock.Elapsed.TotalMilliseconds), null, Compare.None);
        Record("diagSchema", firstDiag.Schema, null, Compare.None);

        // ---- b. settle: let JIT and the first GCs finish ----
        ParkMouseFarFromNotch();
        Thread.Sleep(TimeSpan.FromSeconds(6));
        _app.Process.Refresh();
        Record("settledWorkingSetMb", ToMb(_app.Process.WorkingSet64), null, Compare.None);
        Record("settledPrivateBytesMb", ToMb(_app.Process.PrivateMemorySize64), null, Compare.None);

        // ---- c. idle window: CPU, memory high-water marks, allocation / GC / frame rates ----
        var idleSeconds = 15;
        var idleDiagBefore = _app.ReadDiag();
        _app.Process.Refresh();
        var cpuBefore = _app.Process.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        long maxWorkingSet = 0;
        long maxPrivate = 0;
        for (var i = 0; i < idleSeconds; i++)
        {
            Thread.Sleep(1000);
            _app.Process.Refresh();
            maxWorkingSet = Math.Max(maxWorkingSet, _app.Process.WorkingSet64);
            maxPrivate = Math.Max(maxPrivate, _app.Process.PrivateMemorySize64);
        }
        _app.Process.Refresh();
        var cpuAfter = _app.Process.TotalProcessorTime;
        var wallSeconds = wall.Elapsed.TotalSeconds;
        var idleDiagAfter = _app.ReadDiag();

        var idleCpuPercent = (cpuAfter - cpuBefore).TotalSeconds / wallSeconds * 100.0;
        var idleAllocKbPerSecond = (idleDiagAfter.AllocatedBytes - idleDiagBefore.AllocatedBytes) / 1024.0 / wallSeconds;
        var idleGen0PerMinute = (idleDiagAfter.Gen0 - idleDiagBefore.Gen0) * 60.0 / wallSeconds;
        var hiddenFramesPerSecond = (idleDiagAfter.NotchFrames - idleDiagBefore.NotchFrames) / wallSeconds;

        Record("idleWallSeconds", Math.Round(wallSeconds, 2), null, Compare.None);
        Record("idleCpuPercent", Math.Round(idleCpuPercent, 3), budgets.IdleCpuPercent, Compare.AtMost);
        Record("idleWorkingSetMb", ToMb(maxWorkingSet), budgets.IdleWorkingSetMb, Compare.AtMost);
        Record("idlePrivateBytesMb", ToMb(maxPrivate), budgets.IdlePrivateBytesMb, Compare.AtMost);
        Record("idleAllocKbPerSecond", Math.Round(idleAllocKbPerSecond, 2), budgets.IdleAllocKbPerSecond, Compare.AtMost);
        Record("idleGen0PerMinute", Math.Round(idleGen0PerMinute, 2), budgets.IdleGen0PerMinute, Compare.AtMost);
        Record("idleGen1Delta", idleDiagAfter.Gen1 - idleDiagBefore.Gen1, null, Compare.None);
        Record("idleGen2Delta", idleDiagAfter.Gen2 - idleDiagBefore.Gen2, null, Compare.None);
        Record("idleHeapMb", ToMb(idleDiagAfter.HeapBytes), null, Compare.None);
        Record("idlePacerSkippedDelta", idleDiagAfter.PacerSkipped - idleDiagBefore.PacerSkipped, null, Compare.None);
        Record("hiddenFramesPerSecond", Math.Round(hiddenFramesPerSecond, 3), budgets.HiddenFramesPerSecond, Compare.AtMost);
        RecordFlag("idleFaceState", idleDiagAfter.FaceState, expected: Hidden,
            pass: string.Equals(idleDiagAfter.FaceState, Hidden, StringComparison.Ordinal));
        RecordFlag("idleNotchRendering", idleDiagAfter.NotchRendering, expected: false, pass: !idleDiagAfter.NotchRendering);

        // ---- d. reveal: hover the top-centre strip of the notch window ----
        var bounds = RefreshNotchBounds(notch);
        Log($"notch bounds: {bounds}");
        var hoverPoint = new Point(bounds.Left + bounds.Width / 2, bounds.Top + 2);
        Mouse.MoveTo(hoverPoint);
        Thread.Sleep(TimeSpan.FromMilliseconds(1500));

        var revealDiag = _app.ReadDiag();
        RecordFlag("revealFaceState", revealDiag.FaceState, expected: "not " + Hidden,
            pass: !string.Equals(revealDiag.FaceState, Hidden, StringComparison.Ordinal));
        RecordFlag("revealNotchRendering", revealDiag.NotchRendering, expected: true, pass: revealDiag.NotchRendering);

        // Frame rate over the probe's own clock (uptimeMs), not the test's wall clock: the probe writes from the
        // UI dispatcher, so a write that lands late must shrink the measured time along with the frame count.
        // The window is at least RevealSampleSeconds of probe time, however long that takes to arrive.
        var revealWall = Stopwatch.StartNew();
        var revealBefore = _app.ReadDiag();
        var revealAfter = revealBefore;
        var sampled = PerfAppFixture.WaitUntil(
            () => (revealAfter = _app.ReadDiag()).UptimeMs - revealBefore.UptimeMs >= RevealSampleSeconds * 1000,
            RevealSampleTimeout,
            pollMs: 100);
        var revealSeconds = (revealAfter.UptimeMs - revealBefore.UptimeMs) / 1000.0;
        Record("revealSampleSeconds", Math.Round(revealSeconds, 2), null, Compare.None);
        Record("revealWallSeconds", Math.Round(revealWall.Elapsed.TotalSeconds, 2), null, Compare.None);
        RecordFlag("revealSampleComplete", sampled, expected: true, pass: sampled);
        var revealFramesPerSecond = revealSeconds > 0
            ? (revealAfter.NotchFrames - revealBefore.NotchFrames) / revealSeconds
            : 0;
        Record("revealFramesPerSecond", Math.Round(revealFramesPerSecond, 2), budgets.RevealFramesPerSecondMin, Compare.AtLeast);

        var revealBounds = RefreshNotchBounds(notch);
        var shot = _app.CaptureRectangle("reveal", revealBounds);
        Log($"reveal screenshot: {shot} ({revealBounds})");

        // ---- e. retract: park the mouse far away; the loop must stop once hidden ----
        ParkMouseFarFromNotch();
        Thread.Sleep(TimeSpan.FromSeconds(3));
        var retractWall = Stopwatch.StartNew();
        var retractBefore = _app.ReadDiag();
        Thread.Sleep(TimeSpan.FromSeconds(3));
        var retractAfter = _app.ReadDiag();
        var retractSeconds = retractWall.Elapsed.TotalSeconds;
        var hiddenAfterRetractFramesPerSecond = (retractAfter.NotchFrames - retractBefore.NotchFrames) / retractSeconds;
        Record("hiddenAfterRetractFramesPerSecond", Math.Round(hiddenAfterRetractFramesPerSecond, 3), budgets.HiddenFramesPerSecond, Compare.AtMost);
        RecordFlag("retractFaceState", retractAfter.FaceState, expected: Hidden,
            pass: string.Equals(retractAfter.FaceState, Hidden, StringComparison.Ordinal));
        RecordFlag("retractNotchRendering", retractAfter.NotchRendering, expected: false, pass: !retractAfter.NotchRendering);

        // ---- f. informational process counters ----
        _app.Process.Refresh();
        Record("threads", _app.Process.Threads.Count, null, Compare.None);
        Record("handles", _app.Process.HandleCount, null, Compare.None);
        Record("finalWorkingSetMb", ToMb(_app.Process.WorkingSet64), null, Compare.None);
        Record("finalPrivateBytesMb", ToMb(_app.Process.PrivateMemorySize64), null, Compare.None);
        Record("totalNotchFrames", retractAfter.NotchFrames, null, Compare.None);
        Record("uptimeMs", retractAfter.UptimeMs, null, Compare.None);

        Log(_failures.Count == 0
            ? "all perf budgets met"
            : $"{_failures.Count} perf budget breach(es):\n  " + string.Join("\n  ", _failures));

        Assert.True(_failures.Count == 0,
            $"{_failures.Count} perf budget breach(es) (see perf-results.md in {_app.PerfDirectory}):\n" + string.Join("\n", _failures));
    }

    private enum Compare
    {
        None,
        AtMost,
        AtLeast,
    }

    private void Record(string name, double value, double? budget, Compare compare)
    {
        if (budget is null || compare == Compare.None)
        {
            Log($"{name} = {F(value)}");
            _app.WriteResult(name, value);
            return;
        }

        var limit = budget.Value;
        var pass = compare == Compare.AtMost ? value <= limit : value >= limit;
        var sign = compare == Compare.AtMost ? "<=" : ">=";
        var budgetText = $"{sign} {F(limit)}";
        Log($"{name} = {F(value)} (budget {budgetText}) {(pass ? "PASS" : "FAIL")}");
        _app.WriteResult(name, value, budgetText, pass);
        if (!pass)
        {
            _failures.Add($"{name}: {F(value)} exceeds budget {budgetText}");
        }
    }

    private void RecordFlag(string name, object value, object expected, bool pass)
    {
        var budgetText = $"== {expected}";
        Log($"{name} = {value} (expected {budgetText}) {(pass ? "PASS" : "FAIL")}");
        _app.WriteResult(name, value, budgetText, pass);
        if (!pass)
        {
            _failures.Add($"{name}: was '{value}', expected {budgetText}");
        }
    }

    private Rectangle RefreshNotchBounds(Window notch)
    {
        try
        {
            var current = _app.FindNotch() ?? notch;
            var rect = current.BoundingRectangle;
            if (rect.Width > 0 && rect.Height > 0)
            {
                return rect;
            }
        }
        catch (Exception ex)
        {
            Log($"notch bounds unavailable via UIA ({ex.GetType().Name}: {ex.Message}); falling back to top-centre of the primary screen");
        }

        // The retracted notch sits at the top edge, horizontally centred: fall back to a strip there.
        var desktop = _app.Automation.GetDesktop().BoundingRectangle;
        return new Rectangle(desktop.Left + desktop.Width / 2 - 160, desktop.Top, 320, 48);
    }

    private void ParkMouseFarFromNotch()
    {
        var desktop = _app.Automation.GetDesktop().BoundingRectangle;
        Mouse.MoveTo(new Point(desktop.Right - 4, desktop.Bottom - 4));
    }

    private static double ToMb(long bytes) => Math.Round(bytes / 1024.0 / 1024.0, 1);

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private void Log(string message)
    {
        _output.WriteLine($"[{_app.StartClock.Elapsed.TotalSeconds,8:0.00}s] {message}");
    }
}
