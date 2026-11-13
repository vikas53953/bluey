using System.Diagnostics;
using System.Globalization;
using System.Text;
using Boo.Demo.Tests;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace Boo.VerifyLive;

/// <summary>
/// Records the notch's face over time: a background thread reads the notch window's UI Automation ItemStatus (the
/// NotchFaceState name Boo publishes: "Hidden", "Hover", "Idle", "Thinking", "Responding", "Capture", ...) about every
/// 25 ms and keeps every read, with its time, in memory. <see cref="Dispose"/> writes the timeline to the evidence folder
/// as &lt;name&gt;-face-timeline.txt (one line per run of the same face, plus any <see cref="Mark"/>s).
///
/// It is a RECORDER: the journeys use it to add evidence, not new pass/fail rules. It reads, never clicks or types,
/// and uses its own UI Automation connection so it never shares an element object with the test thread.
///
/// Race rule (tests/Boo.Demo.Tests/DemoTests.cs, the 04-focus nudge wait): a wait may only end on a state read taken
/// after the last separate UI Automation call. Each sample here is ONE UI Automation round trip (the ItemStatus read on
/// the notch window) and nothing is read after it, so <see cref="WaitForFace"/> ends only on a sample whose read started
/// after the wait began (or after <c>sinceMs</c>, which a caller that made its own UI Automation calls sets to
/// <see cref="NowMs"/> taken after its last call). An older sample can never end a wait.
///
/// Limit: a face shown for less than one sampling interval (about 25-35 ms, Windows timer resolution) can be missed.
/// </summary>
internal sealed class FaceTimeline : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(25);
    public const string Missing = "<no notch>";
    public const string Unreadable = "<unreadable>";

    /// <summary>One read: when it started (ms since the recorder started), what it returned, and how long it took.</summary>
    public readonly record struct Sample(double AtMs, string Face, double ReadMs);

    private readonly record struct Note(double AtMs, string Text);

    private readonly BooAppFixture _boo;
    private readonly string _name;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly object _gate = new();
    private readonly List<Sample> _samples = [];
    private readonly List<Note> _notes = [];
    private readonly Thread _thread;
    private volatile bool _stopping;
    private int _readErrors;
    private bool _disposed;

    private FaceTimeline(BooAppFixture boo, string name)
    {
        _boo = boo;
        _name = name;
        _thread = new Thread(Run) { IsBackground = true, Name = "face-timeline " + name };
    }

    /// <summary>Starts recording now. Dispose (a <c>using</c>) stops it and writes the timeline file.</summary>
    public static FaceTimeline Start(BooAppFixture boo, string name)
    {
        var timeline = new FaceTimeline(boo, name);
        timeline._thread.Start();
        return timeline;
    }

    /// <summary>Milliseconds since the recorder started, on the same clock as every sample.</summary>
    public double NowMs => _clock.Elapsed.TotalMilliseconds;

    /// <summary>Adds a labelled moment (for example "question sent") to the written timeline.</summary>
    public void Mark(string text)
    {
        lock (_gate) _notes.Add(new Note(NowMs, text));
    }

    /// <summary>
    /// Waits for a sample showing <paramref name="face"/> whose read started at or after <paramref name="sinceMs"/>
    /// (default: now, when the wait begins). Returns that sample, or null on timeout. Never throws for a timeout and
    /// asserts nothing: the caller decides what a missing face means.
    /// </summary>
    public Sample? WaitForFace(string face, TimeSpan timeout, double? sinceMs = null)
    {
        var since = sinceMs ?? NowMs;
        var deadline = NowMs + timeout.TotalMilliseconds;
        lock (_gate)
        {
            var checkedUpTo = 0;
            while (true)
            {
                for (; checkedUpTo < _samples.Count; checkedUpTo++)
                {
                    var s = _samples[checkedUpTo];
                    if (s.AtMs >= since && string.Equals(s.Face, face, StringComparison.Ordinal)) return s;
                }
                var left = deadline - NowMs;
                if (left <= 0 || _stopping) return null;
                Monitor.Wait(_gate, TimeSpan.FromMilliseconds(Math.Min(left, 250)));
            }
        }
    }

    /// <summary>The faces seen in order (repeats collapsed) between two times, for observations.</summary>
    public IReadOnlyList<string> FacesSeen(double fromMs = 0, double toMs = double.MaxValue)
    {
        var seen = new List<string>();
        lock (_gate)
        {
            foreach (var s in _samples)
            {
                if (s.AtMs < fromMs || s.AtMs > toMs) continue;
                if (seen.Count == 0 || seen[^1] != s.Face) seen.Add(s.Face);
            }
        }
        return seen;
    }

    private void Run()
    {
        UIA3Automation? automation = null;
        try
        {
            automation = new UIA3Automation();
            AutomationElement? notch = null;
            while (!_stopping)
            {
                var started = NowMs;
                string face;
                try
                {
                    notch ??= FindNotch(automation);
                    face = notch is null ? Missing : notch.Properties.ItemStatus.ValueOrDefault ?? string.Empty;
                }
                catch (Exception)
                {
                    // The window may have been recreated or Boo may be busy; find it again on the next read.
                    notch = null;
                    face = Unreadable;
                    Interlocked.Increment(ref _readErrors);
                }
                lock (_gate)
                {
                    _samples.Add(new Sample(started, face, NowMs - started));
                    Monitor.PulseAll(_gate);
                }
                var wait = started + Interval.TotalMilliseconds - NowMs;
                if (wait > 0) Thread.Sleep(TimeSpan.FromMilliseconds(wait));
            }
        }
        catch (Exception ex)
        {
            lock (_gate) _notes.Add(new Note(NowMs, "RECORDER STOPPED: " + ex.GetType().Name + ": " + ex.Message));
        }
        finally
        {
            automation?.Dispose();
            lock (_gate) Monitor.PulseAll(_gate);
        }
    }

    private Window? FindNotch(UIA3Automation automation) =>
        _boo.App.GetAllTopLevelWindows(automation).FirstOrDefault(w =>
            BooAppFixture.SafeName(w) is var n &&
            (string.Equals(n, BooAppFixture.NotchName, StringComparison.Ordinal) || string.Equals(n, BooAppFixture.NotchTitle, StringComparison.Ordinal)));

    /// <summary>The timeline as text: a header, then one line per run of the same face, with marks in time order.</summary>
    public string Render()
    {
        List<Sample> samples;
        List<Note> notes;
        lock (_gate)
        {
            samples = [.. _samples];
            notes = [.. _notes];
        }
        var inv = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.AppendLine(inv, $"face timeline '{_name}': notch ItemStatus read by UI Automation every ~{Interval.TotalMilliseconds:0} ms (recorder only; asserts nothing)");
        text.AppendLine(inv, $"started {_startedAt:yyyy-MM-ddTHH:mm:ss.fffZ}; samples={samples.Count}; readErrors={_readErrors}");
        if (samples.Count > 1)
        {
            var span = samples[^1].AtMs - samples[0].AtMs;
            text.AppendLine(inv, $"mean interval={span / (samples.Count - 1):0.0} ms; mean read={samples.Average(s => s.ReadMs):0.0} ms; max read={samples.Max(s => s.ReadMs):0.0} ms");
        }
        text.AppendLine(inv, $"faces in order: {string.Join(" > ", FacesSeen())}");
        text.AppendLine("time from start (ms) | face | lasted (ms) | samples   (a face shorter than one interval can be missed)");

        var lines = new List<(double At, string Line)>();
        for (var i = 0; i < samples.Count;)
        {
            var j = i;
            while (j + 1 < samples.Count && samples[j + 1].Face == samples[i].Face) j++;
            var end = j + 1 < samples.Count ? samples[j + 1].AtMs : samples[j].AtMs;
            lines.Add((samples[i].AtMs, string.Format(inv, "+{0,8:0} | {1,-12} | {2,7:0} | {3}", samples[i].AtMs, samples[i].Face, end - samples[i].AtMs, j - i + 1)));
            i = j + 1;
        }
        foreach (var n in notes) lines.Add((n.AtMs, string.Format(inv, "+{0,8:0} | -- mark: {1}", n.AtMs, n.Text)));
        foreach (var (_, line) in lines.OrderBy(l => l.At)) text.AppendLine(line);
        return text.ToString();
    }

    /// <summary>Stops the recorder and writes &lt;name&gt;-face-timeline.txt. Never throws: evidence must not mask the step's own result.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopping = true;
        _thread.Join(TimeSpan.FromSeconds(5));
        try
        {
            _boo.WriteObservation(_name + "-face-timeline", Render());
        }
        catch (Exception)
        {
            // Best effort: the journey's own result is what matters.
        }
    }
}
