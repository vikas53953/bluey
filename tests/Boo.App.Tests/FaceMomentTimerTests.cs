using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Boo.App;
using Boo.Core;
using Xunit.Abstractions;
using Application = System.Windows.Application;

namespace Boo.App.Tests;

/// <summary>
/// Issue #111: the Happy face after a focus session ends stayed on 36-91 ms past its 2.5 s deadline on hosted runs.
/// The schedule's deadline was right; the expiry tick was a Background-priority DispatcherTimer, which WPF only
/// dequeues when no input or posted message is pending and after all Render/Input/Loaded work. The fix runs that
/// timer at Normal priority. The real MainWindow runs in-process on one STA dispatcher; the hold is armed through
/// the app's own SetFace (the path focus End, replies and nudges use) and observed through FaceInputsChanged.
///
/// Three kinds of check, kept apart:
///   1. a configuration regression guard (the timer's priority is exactly Normal);
///   2. dispatcher-integration observations through the real timer (each must receive its events, or it fails);
///   3. a measured frame-load sample with a declared N that reports a distribution and asserts no latency bound.
/// None of these replaces the published Boo.exe journeys (Q13H/Q13S).
/// </summary>
public sealed class FaceMomentTimerTests
{
    private readonly ITestOutputHelper _output;
    public FaceMomentTimerTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Configuration regression guard, not proof of user-visible timing: the face-moment timer runs at exactly
    /// DispatcherPriority.Normal (above Render, Loaded and Input, not gated by pending input). Red on the Background
    /// timer that issue #111 measured.
    /// </summary>
    [Fact]
    public void TheFaceMomentTimer_RunsAtExactlyNormalPriority()
    {
        var priority = RunWithWindow((window, _) =>
        {
            Hold(window, Current(window) with { Happy = true }, TimeSpan.FromSeconds(2.5), FaceMomentKind.Happy);
            return PriorityOf(Timer(window));
        });

        Assert.Equal(DispatcherPriority.Normal, priority);
    }

    /// <summary>
    /// Integration observation through the real DispatcherTimer: overlapping Responding and Happy holds each clear
    /// once, Responding first, and neither is published before its deadline. The lower bound allows one
    /// Environment.TickCount64 step (the schedule's clock), measured from just before the hold was armed.
    /// </summary>
    [Fact]
    public void OverlappingRespondingAndHappyHolds_EachClearOnce_InOrder_NotBeforeTheirDeadlines()
    {
        var log = RunWithWindow((window, recorder) =>
        {
            var armed = Stopwatch.GetTimestamp();
            Hold(window, Current(window) with { Responding = true }, TimeSpan.FromMilliseconds(300), FaceMomentKind.Responding);
            Hold(window, Current(window) with { Happy = true }, TimeSpan.FromMilliseconds(700), FaceMomentKind.Happy);
            Assert.True(recorder.WaitFor(() => recorder.Has(i => !i.Happy && !i.Responding), TimeSpan.FromSeconds(5)),
                "missing event: Happy and Responding were not both cleared by the real timer within 5 s");
            recorder.WaitFor(() => false, TimeSpan.FromMilliseconds(300)); // absence window: no second clear
            return recorder.Since(armed);
        });

        var respondingOff = Assert.Single(log, e => e.Previous.Responding && !e.Inputs.Responding);
        var happyOff = Assert.Single(log, e => e.Previous.Happy && !e.Inputs.Happy);
        Assert.True(respondingOff.Ms < happyOff.Ms, $"Responding off at {respondingOff.Ms:F1} ms, Happy off at {happyOff.Ms:F1} ms");
        Assert.True(respondingOff.Ms >= 300 - TickStepMs, $"Responding cleared early: {respondingOff.Ms:F1} ms");
        Assert.True(happyOff.Ms >= 700 - TickStepMs, $"Happy cleared early: {happyOff.Ms:F1} ms");
        _output.WriteLine($"observed: Responding off {respondingOff.Ms:F1} ms (300), Happy off {happyOff.Ms:F1} ms (700)");
    }

    /// <summary>
    /// Integration observation: a re-hold moves the pending Happy expiry later, and a Clear (Happy switched off)
    /// cancels the armed timer so no stale clear is published afterwards.
    /// </summary>
    [Fact]
    public void AReHoldMovesThePendingHappyExpiry_AndAClearCancelsIt()
    {
        var (reHeld, afterClear) = RunWithWindow((window, recorder) =>
        {
            var armed = Stopwatch.GetTimestamp();
            Hold(window, Current(window) with { Happy = true }, TimeSpan.FromMilliseconds(300), FaceMomentKind.Happy);
            Hold(window, Current(window) with { Happy = true }, TimeSpan.FromMilliseconds(800), FaceMomentKind.Happy);
            Assert.True(recorder.WaitFor(() => recorder.Has(i => !i.Happy), TimeSpan.FromSeconds(5)),
                "missing event: the re-held Happy was never cleared by the real timer");
            var reHeldOff = Assert.Single(recorder.Since(armed), e => e.Previous.Happy && !e.Inputs.Happy).Ms;

            Hold(window, Current(window) with { Happy = true }, TimeSpan.FromMilliseconds(300), FaceMomentKind.Happy);
            Hold(window, Current(window) with { Happy = false }, null, FaceMomentKind.None);
            var cleared = Stopwatch.GetTimestamp(); // after the Clear's own (Happy off) publication
            recorder.WaitFor(() => false, TimeSpan.FromMilliseconds(700)); // absence window past the cancelled deadline
            return (reHeldOff, recorder.Since(cleared).Count);
        });

        Assert.True(reHeld >= 800 - TickStepMs, $"the first (300 ms) deadline still cleared Happy at {reHeld:F1} ms");
        Assert.Equal(0, afterClear);
    }

    /// <summary>
    /// Integration observation around shutdown with a real, armed timer: DisposeShell (through StopAll's final SetFace)
    /// leaves the face-moment timer stopped, and nothing is published after it returns, even past the old deadline.
    /// This was already green before #111's change (StopAll clears every moment); the explicit stop is defensive.
    /// </summary>
    [Fact]
    public void DisposeShellWithAnArmedHappyHold_LeavesTheTimerStopped_AndPublishesNothingAfterwards()
    {
        var (enabled, published) = RunWithWindow((window, recorder) =>
        {
            Hold(window, Current(window) with { Happy = true }, TimeSpan.FromMilliseconds(400), FaceMomentKind.Happy);
            Assert.True(Timer(window).IsEnabled, "precondition: the Happy hold armed the real timer");
            window.DisposeShell();
            var disposed = Stopwatch.GetTimestamp();
            var timerEnabled = Timer(window).IsEnabled;
            recorder.WaitFor(() => false, TimeSpan.FromMilliseconds(800)); // absence window past the old deadline
            return (timerEnabled, recorder.Since(disposed).Count);
        }, disposeAtEnd: false);

        Assert.False(enabled);
        Assert.Equal(0, published);
    }

    /// <summary>
    /// Measured sample, no pass threshold. Declared N = 16 Happy holds of 500 ms, each armed from inside a
    /// CompositionTarget.Rendering callback that does about 8 ms of work per frame (the notch frame loop's shape),
    /// at phase 0..7 ms into that work (each phase twice), so adverse alignments are covered. Reports, per sample,
    /// Happy-off publication minus (hold armed + 500 ms) on one Stopwatch scale. Fails only on a missing event.
    /// </summary>
    [Fact]
    public void MeasuredSample_HappyClearLatencyUnderFrameLoad_N16()
    {
        const int N = 16;
        const int HoldMs = 500;
        const int WorkMs = 8;
        var samples = RunWithWindow((window, recorder) =>
        {
            var results = new List<(int Phase, double LateMs)>();
            Hold(window, Current(window) with { Happy = false }, null, FaceMomentKind.None);
            _ = Timer(window); // records the priority under test for the report
            var index = 0;
            long armedAt = 0;
            var waitingToArm = true;
            void OnFrame(object? sender, EventArgs e)
            {
                var start = Stopwatch.GetTimestamp();
                if (waitingToArm && index < N)
                {
                    Spin(start, index % 8);
                    armedAt = Stopwatch.GetTimestamp();
                    Hold(window, Current(window) with { Happy = true }, TimeSpan.FromMilliseconds(HoldMs), FaceMomentKind.Happy);
                    waitingToArm = false;
                }
                Spin(start, WorkMs);
            }

            System.Windows.Media.CompositionTarget.Rendering += OnFrame;
            try
            {
                for (; index < N; index++)
                {
                    waitingToArm = true;
                    Assert.True(recorder.WaitFor(() => !waitingToArm, TimeSpan.FromSeconds(5)),
                        $"missing frame: sample {index} was never armed by a Rendering callback");
                    var armed = armedAt;
                    Assert.True(recorder.WaitFor(() => recorder.Since(armed).Any(r => r.Previous.Happy && !r.Inputs.Happy), TimeSpan.FromSeconds(5)),
                        $"missing event: sample {index} Happy was never cleared");
                    var off = recorder.Since(armed).First(r => r.Previous.Happy && !r.Inputs.Happy).Ms;
                    results.Add((index % 8, off - HoldMs));
                }
            }
            finally
            {
                System.Windows.Media.CompositionTarget.Rendering -= OnFrame;
            }
            return results;
        });

        Assert.Equal(N, samples.Count);
        var sorted = samples.Select(s => s.LateMs).OrderBy(v => v).ToArray();
        _output.WriteLine($"#111 measured sample, N={N}, hold {HoldMs} ms, frame work {WorkMs} ms; "
            + $"timer priority under test = {TimerPriorityName}");
        _output.WriteLine($"late ms: min {sorted[0]:F1}, median {sorted[N / 2]:F1}, max {sorted[^1]:F1}");
        foreach (var (phase, late) in samples)
        {
            _output.WriteLine($"phase {phase} ms -> late {late:F1} ms");
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>One Environment.TickCount64 step (the schedule clock) on Windows' default timer resolution.</summary>
    private const double TickStepMs = 16;

    private static string TimerPriorityName = "<not created>";

    private static readonly MethodInfo SetFaceMethod = typeof(MainWindow).GetMethod("SetFace", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("MainWindow.SetFace not found");
    private static readonly FieldInfo FaceInputsField = typeof(MainWindow).GetField("_faceInputs", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("MainWindow._faceInputs not found");
    private static readonly FieldInfo TimerField = typeof(MainWindow).GetField("_faceMomentTimer", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("MainWindow._faceMomentTimer not found");

    /// <summary>DispatcherTimer keeps its priority in a private field (WPF exposes no getter); read it, never set it.</summary>
    private static readonly FieldInfo DispatcherTimerPriorityField = typeof(DispatcherTimer).GetField("_priority", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("DispatcherTimer._priority not found");

    private static DispatcherPriority PriorityOf(DispatcherTimer timer) => (DispatcherPriority)DispatcherTimerPriorityField.GetValue(timer)!;

    private static FaceInputs Current(MainWindow window) => (FaceInputs)FaceInputsField.GetValue(window)!;

    private static DispatcherTimer Timer(MainWindow window)
    {
        var timer = (DispatcherTimer?)TimerField.GetValue(window) ?? throw new InvalidOperationException("the face-moment timer was never created");
        TimerPriorityName = PriorityOf(timer).ToString();
        return timer;
    }

    private static void Hold(MainWindow window, FaceInputs next, TimeSpan? duration, FaceMomentKind kind) =>
        SetFaceMethod.Invoke(window, [next, duration, kind]);

    private static void Spin(long start, int milliseconds)
    {
        var until = start + milliseconds * Stopwatch.Frequency / 1000;
        while (Stopwatch.GetTimestamp() < until)
        {
        }
    }

    private sealed record FaceEvent(long At, FaceInputs Previous, FaceInputs Inputs);
    private sealed record Observed(double Ms, FaceInputs Previous, FaceInputs Inputs);

    /// <summary>Records FaceInputsChanged on the Stopwatch scale and pumps the dispatcher without sleeping.</summary>
    private sealed class Recorder
    {
        private readonly List<FaceEvent> _events = new();
        private FaceInputs _last;
        private Action? _check;

        public Recorder(MainWindow window)
        {
            _last = Current(window);
            window.FaceInputsChanged += inputs =>
            {
                _events.Add(new FaceEvent(Stopwatch.GetTimestamp(), _last, inputs));
                _last = inputs;
                _check?.Invoke();
            };
        }

        public bool Has(Func<FaceInputs, bool> predicate) => _events.Count > 0 && predicate(_events[^1].Inputs);

        public List<Observed> Since(long origin) => _events.Where(e => e.At >= origin)
            .Select(e => new Observed((e.At - origin) * 1000.0 / Stopwatch.Frequency, e.Previous, e.Inputs)).ToList();

        /// <summary>
        /// Pumps until done() (checked on every face event and every Rendering-free dispatcher turn via the timeout
        /// timer) or the timeout. No Thread.Sleep on the UI thread, so the timer under test is not delayed by the pump.
        /// </summary>
        public bool WaitFor(Func<bool> done, TimeSpan timeout)
        {
            if (done()) return true;
            var frame = new DispatcherFrame();
            _check = () => { if (done()) frame.Continue = false; };
            var poll = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(50) };
            var giveUp = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
            poll.Tick += (_, _) =>
            {
                if (done() || Stopwatch.GetTimestamp() >= giveUp) frame.Continue = false;
            };
            poll.Start();
            Dispatcher.PushFrame(frame);
            poll.Stop();
            _check = null;
            return done();
        }
    }

    private static T RunWithWindow<T>(Func<MainWindow, Recorder, T> body, bool disposeAtEnd = true) => RunOnStaThread(() =>
    {
        EnsureApplicationResources();
        var dataDirectory = Directory.CreateTempSubdirectory("boo-app-face-moment-").FullName;
        var window = new MainWindow(
            dataDirectory,
            onClose: () => { },
            onStop: () => { },
            onDisplayChanged: _ => { },
            verticalOffset: () => 0,
            ownedWindows: () => Array.Empty<long>(),
            captureExclusion: () => true,
            onCaptureActivity: (_, _) => { },
            onMicrophoneActivity: _ => { },
            onAppearanceChanged: _ => { });
        try
        {
            window.ShowActivated = false;
            window.Show();
            var loaded = new Recorder(window);
            loaded.WaitFor(() => window.IsLoaded, TimeSpan.FromSeconds(30));
            Assert.True(window.IsLoaded, "the MainWindow never loaded");
            return body(window, loaded);
        }
        finally
        {
            if (disposeAtEnd) window.DisposeShell();
            window.Close();
        }
    });

    /// <summary>MainWindow.xaml needs App.xaml's resources; the Application is never Run(), so OnStartup never runs.</summary>
    private static void EnsureApplicationResources()
    {
        if (Application.Current is not null)
        {
            return;
        }
        var app = new App();
        app.InitializeComponent();
    }

    private static T RunOnStaThread<T>(Func<T> body)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(120)))
        {
            throw new TimeoutException("The STA test thread did not finish within 120 s.");
        }
        if (failure is not null)
        {
            throw new InvalidOperationException("The STA test body failed: " + failure.Message, failure);
        }
        return result!;
    }
}
