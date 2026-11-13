using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using Boo.Core;
using Boo.Demo.Tests;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Xunit.Abstractions;

namespace Boo.Explorer;

/// <summary>
/// The run's budget: at most <see cref="HardCap"/> requests from launches that hold the REAL OpenAI key, enforced at
/// ADMISSION. Before a real-key question the harness requires: a healthy meter, a quiet Boo (every request it started
/// has stopped), speech off (no voice session), and fewer than <see cref="HardCap"/> admissions so far. Each admission
/// allows exactly one request on that Boo (<see cref="RequestMeter.AllowOne"/>); the meter, reading Boo's own network
/// events, ends the run if Boo starts anything it was not allowed. The ledger lists admitted questions next to what Boo
/// really started, stopped and failed. The explorer never retries a question.
/// </summary>
public static class ExplorerLedger
{
    public const int HardCap = 20;
    private static readonly object Gate = new();
    private static readonly List<string> Asked = new();
    private static int s_admitted;

    static ExplorerLedger()
    {
        // Stop and drain every meter session at the end of the run, so the final ledger holds every delivered event.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RequestMeter.DrainAll();
    }

    public static int Admitted => Volatile.Read(ref s_admitted);

    /// <summary>Admits one question on <paramref name="meter"/>'s Boo, or throws (nothing is typed).</summary>
    public static void Admit(string what, RequestMeter meter, bool billable)
    {
        lock (Gate)
        {
            if (meter.Problem is { } problem)
            {
                Write();
                throw new InvalidOperationException($"FAIL CLOSED: refusing '{what}': {problem}");
            }
            if (billable)
            {
                if (s_admitted >= HardCap)
                {
                    Write();
                    throw new InvalidOperationException($"BUDGET: refusing '{what}': {s_admitted} of {HardCap} real-key requests already admitted.");
                }
                s_admitted++;
                meter.AllowOne();
            }
            Asked.Add($"{DateTime.UtcNow:HH:mm:ss.fff} {(billable ? $"real key, admitted #{s_admitted}" : "no/wrong key or offline")}: {what}");
            Write();
        }
    }

    public static void Flush()
    {
        lock (Gate) Write();
    }

    private static void Write()
    {
        var dir = Environment.GetEnvironmentVariable("BOO_UI_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        var events = RequestMeter.Events;
        var sb = new StringBuilder();
        sb.AppendLine($"real-key requests ADMITTED (the cap, enforced before each ask): {s_admitted} of {HardCap}");
        sb.AppendLine($"real-key requests Boo STARTED (seen at its network boundary): {RequestMeter.BillableStarted}");
        sb.AppendLine($"all requests Boo started: {events.Count(e => e.Contains(" START "))}; answered with an HTTP status: {events.Count(e => e.Contains(" STOP ") && !e.Contains("HTTP -1"))}; failed or cancelled: {events.Count(e => e.Contains(" FAILED "))}");
        sb.AppendLine("meter problems: " + string.Join("; ", RequestMeter.Problems.DefaultIfEmpty("none")));
        sb.AppendLine("not seen by the meter: voice turns over an open WebSocket (voice is kept off; opening a voice session is a request and would be unadmitted)");
        sb.AppendLine("retries: 0 (the harness never re-asks)");
        sb.AppendLine("--- asked by the harness ---");
        Asked.ForEach(a => sb.AppendLine("  " + a));
        sb.AppendLine("--- what Boo really started (System.Net.Http events, each with its own time) ---");
        foreach (var e in events) sb.AppendLine("  " + e);
        File.WriteAllText(Path.Combine(dir, "explorer-ledger.txt"), sb.ToString());
    }
}

/// <summary>Samples the notch's face (UI Automation ItemStatus) about every 40 ms on a background thread.</summary>
public sealed class FaceSampler : IDisposable
{
    public static readonly string[] KnownFaces =
        ["Hidden", "Hover", "Idle", "Listening", "Thinking", "Responding", "Capture", "Nudge", "Paused", "Happy"];

    private static readonly ConcurrentDictionary<string, int> SeenAllRun = new();
    private readonly List<(long Ms, string Face)> _changes = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Thread _thread;
    private volatile bool _stop;

    public FaceSampler(BooAppFixture boo)
    {
        _thread = new Thread(() =>
        {
            AutomationElement? notch = null;
            var last = "<none>";
            while (!_stop)
            {
                try
                {
                    notch ??= boo.FindNotch();
                    var face = notch?.Properties.ItemStatus.ValueOrDefault ?? "<no notch>";
                    if (face != last)
                    {
                        lock (_changes) _changes.Add((_clock.ElapsedMilliseconds, face));
                        SeenAllRun.AddOrUpdate(face, 1, (_, n) => n + 1);
                        last = face;
                    }
                }
                catch (Exception)
                {
                    notch = null; // window recreated or Boo gone; look it up again
                }
                Thread.Sleep(40);
            }
        }) { IsBackground = true, Name = "face-sampler" };
        _thread.Start();
    }

    public IReadOnlyList<string> Faces { get { lock (_changes) return _changes.Select(c => c.Face).ToList(); } }

    public string Timeline()
    {
        lock (_changes) return string.Join(" -> ", _changes.Select(c => $"{c.Face}@{c.Ms}ms"));
    }

    public static string SeenSummary() =>
        string.Join("\n", KnownFaces.Select(f => $"{f}: {(SeenAllRun.TryGetValue(f, out var n) ? $"seen ({n} entries)" : "NOT reached")}"))
        + "\nunexpected values: " + string.Join(", ", SeenAllRun.Keys.Where(k => !KnownFaces.Contains(k)).DefaultIfEmpty("none"));

    public void Dispose()
    {
        _stop = true;
        _thread.Join(2000);
    }
}

/// <summary>The shared moves and the health invariants every explorer step checks.</summary>
public abstract class ExplorerSteps<TFixture> where TFixture : BooAppFixture
{
    protected static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(30);
    protected static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(120);

    protected ExplorerSteps(TFixture boo, ITestOutputHelper output)
    {
        Boo = boo;
        Output = output;
    }

    protected TFixture Boo { get; private set; }
    protected ITestOutputHelper Output { get; }

    /// <summary>Lets a step swap to a relaunched Boo (kill-and-relaunch).</summary>
    protected void UseApp(TFixture boo) => Boo = boo;

    /// <summary>The seeded random source for the guided variations; the seed is written to the evidence.</summary>
    protected static Random Rng(string step)
    {
        var seedText = Environment.GetEnvironmentVariable("BOO_EXPLORER_SEED");
        var seed = int.TryParse(seedText, out var s) ? s : 5353;
        // FNV-1a over the step name: stable across processes (HashCode.Combine is randomised per process).
        var hash = 2166136261u ^ (uint)seed;
        foreach (var c in step)
        {
            hash = (hash ^ c) * 16777619u;
        }
        return new Random((int)hash);
    }

    /// <summary>True for a launch that holds the REAL key: its requests count toward the run's cap.</summary>
    protected virtual bool BillableLaunch => false;

    /// <summary>The meter on the Boo this step drives (attached on first use; fails closed if it cannot attach).</summary>
    protected RequestMeter Meter => RequestMeter.For(Boo.App.ProcessId, GetType().Name, BillableLaunch);

    /// <summary>
    /// Runs a step: records the face timeline, then checks the health invariants even when the body passed. A failure
    /// leaves a -FAILED screenshot, what Boo showed, and the timeline.
    /// </summary>
    protected void Explore(string step, Action<FaceSampler> body)
    {
        using var faces = new FaceSampler(Boo);
        try
        {
            if (!Boo.App.HasExited) _ = Meter; // count Boo's requests from before the first action of the step
            body(faces);
            var problems = HealthProblems();
            Assert.True(problems.Count == 0, "HEALTH: " + string.Join("; ", problems));
        }
        catch (Exception ex)
        {
            Boo.CaptureScreen(step + "-FAILED");
            Boo.WriteObservation(step + "-FAILED", ex + "\n----- what Boo showed -----\n" + WhatBooShows() + "\nfaces: " + faces.Timeline());
            throw;
        }
        finally
        {
            if (!Boo.App.HasExited && !Meter.Drain(TimeSpan.FromSeconds(15)))
            {
                Boo.WriteObservation(step + "-meter", $"requests still in flight after the step: {Meter.InFlight}");
            }
            faces.Dispose();
            Boo.WriteObservation(step + "-faces", faces.Timeline());
            Boo.WriteObservation("explorer-face-coverage", FaceSampler.SeenSummary());
            ExplorerLedger.Flush();
        }
    }

    /// <summary>Set by a step that kills Boo itself (kill-and-relaunch); the dead first launch is then not a finding.</summary>
    protected bool KilledOnPurpose { get; set; }

    /// <summary>Invariants that must hold after ANY user action, in any order.</summary>
    protected List<string> HealthProblems() => KilledOnPurpose && Boo.App.HasExited ? CrashLogProblems(Boo) : HealthProblems(Boo);

    private static List<string> CrashLogProblems(BooAppFixture boo)
    {
        var problems = new List<string>();
        var logs = Path.Combine(boo.DataDirectory, CrashReport.LogsFolderName);
        if (!Directory.Exists(logs)) return problems;
        foreach (var f in Directory.EnumerateFiles(logs))
        {
            var head = string.Join(" | ", File.ReadLines(f).Take(6));
            problems.Add($"crash log {Path.GetFileName(f)}: {Shorten(head, 400)}");
        }
        return problems;
    }

    protected static List<string> HealthProblems(BooAppFixture boo)
    {
        var problems = new List<string>();
        try
        {
            if (boo.App.HasExited) problems.Add($"Boo exited (exit code {boo.App.ExitCode})");
        }
        catch (Exception ex) { problems.Add("cannot read Boo's process: " + ex.Message); }
        problems.AddRange(CrashLogProblems(boo));
        var panels = 0;
        try
        {
            panels = boo.App.GetAllTopLevelWindows(boo.Automation)
                .Count(w => BooAppFixture.SafeName(w).StartsWith(BooAppFixture.PanelTitlePrefix, StringComparison.Ordinal));
        }
        catch (Exception) { /* Boo gone; reported above */ }
        if (panels > 1) problems.Add($"{panels} panel windows are open at once");
        problems.AddRange(RequestMeter.Problems.Select(p => "METER: " + p));
        if (!boo.App.HasExited && boo.FindNotch() is null) problems.Add("the notch window is gone");
        return problems;
    }

    protected string WhatBooShows()
    {
        try
        {
            var panel = Boo.FindPanel();
            if (panel is null) return "panel closed; notch face=" + NotchFace();
            return $"status='{NameOfId(panel, "StatusText")}'\nfocus='{NameOfId(panel, "FocusStatus")}'\n" +
                   $"lastRows='{string.Join(" || ", Rows(panel).TakeLast(4).Select(r => Shorten(r, 200)))}'\nface={NotchFace()}";
        }
        catch (Exception ex) { return "<not readable: " + ex.Message + ">"; }
    }

    // ---- moves -------------------------------------------------------------------------------

    protected string NotchFace()
    {
        try { return Boo.FindNotch()?.Properties.ItemStatus.ValueOrDefault ?? string.Empty; }
        catch (Exception) { return string.Empty; }
    }

    protected Point NotchHoverPoint()
    {
        var rect = Boo.WaitForNotch(UiTimeout).BoundingRectangle;
        return new Point(rect.Left + rect.Width / 2, rect.Top + 2);
    }

    protected Point AwayPoint() => NotchHoverPoint() with { Y = NotchHoverPoint().Y + 400 };

    /// <summary>The notch must come back to rest (Hidden) once nothing is going on and the pointer is away.</summary>
    protected bool SettlesHidden(TimeSpan timeout)
    {
        Mouse.MoveTo(AwayPoint());
        return BooAppFixture.WaitUntil(() => NotchFace() == "Hidden", timeout);
    }

    /// <summary>
    /// Opens the panel the way a user does (Ctrl+Alt+B), but only once Boo's notch exists: the hotkey is registered by
    /// the notch window, so a press before it appears is lost (a harness timing issue, not a Boo bug).
    /// </summary>
    protected Window OpenPanel() => OpenPanel(Boo);

    protected static Window OpenPanel(BooAppFixture boo)
    {
        boo.WaitForNotch(TimeSpan.FromSeconds(60));
        if (boo.FindPanel() is { } open) return open;
        Thread.Sleep(1000);
        return boo.EnsurePanel();
    }

    protected void ClosePanel()
    {
        if (Boo.FindPanel() is { } panel && TryFind(panel, "CloseButton") is { } close)
        {
            close.AsButton().Invoke();
            BooAppFixture.WaitUntil(() => Boo.FindPanel() is null, UiTimeout);
        }
    }

    protected static void PressStop() =>
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.F12);

    protected void Type(Window panel, string text)
    {
        var box = Find(panel, "InputBox").AsTextBox();
        box.Text = text;
    }

    /// <summary>
    /// Types and sends one question. On a real-key launch the metered request count must be under the cap first
    /// (refused otherwise, nothing typed). The meter, not this call, says what Boo really sent.
    /// </summary>
    /// <param name="whilePending">
    /// True only for a question deliberately sent while the previous reply is still on the wire (the caller has just
    /// proved that with <see cref="RequireReplyPending"/>). It is still admitted and counted; it skips the "quiet Boo"
    /// wait, which would otherwise hold it until the first reply finished.
    /// </param>
    protected void Ask(Window panel, string text, string label, bool whilePending = false)
    {
        var meter = Meter;
        if (BillableLaunch)
        {
            // Voice turns travel over an open WebSocket the meter cannot count, so voice must be off.
            var settings = BooAppFixture.ReadShared(Boo.SettingsPath);
            Assert.True(System.Text.RegularExpressions.Regex.IsMatch(settings, "\"SpeechEnabled\"\\s*:\\s*false"),
                "FAIL CLOSED: speech is on in settings.json, so voice turns could bypass the meter. Nothing was asked.");
            // One request per admission: start only from a quiet Boo, unless this question is meant to overlap.
            Assert.True(whilePending || meter.Drain(TimeSpan.FromSeconds(30)),
                $"FAIL CLOSED: Boo still has {meter.InFlight} request(s) in flight before '{label}'. Nothing was asked.");
        }
        ExplorerLedger.Admit(label + ": " + text, meter, BillableLaunch);
        _lastAsk = (meter.Started, BooRows(panel).Count);
        Type(panel, text);
        Find(panel, "SendButton").AsButton().Invoke();
        Output.WriteLine($"asked: {text}");
    }

    private (int Started, int BooRows) _lastAsk;

    /// <summary>
    /// The precondition of every "while Boo is answering" variation: a request is really on the wire (the meter saw it
    /// start and not end) and no answer containing <paramref name="answerWord"/> has arrived yet. If that is never
    /// true, the step fails as NOT REACHED: it says nothing about Boo, and never passes by acting on an idle app.
    /// </summary>
    protected void RequireReplyPending(Window panel, string answerWord, string what)
    {
        // THIS ask's request: one Boo started after the ask, not stopped yet, while the panel still says it is working
        // and no new answer (or error) has been shown.
        var baseline = _lastAsk;
        var newRequest = BooAppFixture.WaitUntil(() => Meter.Started > baseline.Started, TimeSpan.FromSeconds(20));
        var inFlight = Meter.InFlight > 0;
        var status = Status(panel);
        var busy = LooksBusy(status);
        var newRows = BooRows(panel).Count - baseline.BooRows;
        var answered = BooRows(panel).Any(r => r.Contains(answerWord, StringComparison.OrdinalIgnoreCase));
        Output.WriteLine($"{what}: newRequest={newRequest} inFlight={inFlight} busy={busy} status='{status}' newRows={newRows} answered={answered}");
        if (!newRequest || !inFlight || !busy || newRows > 0 || answered)
        {
            Assert.Fail($"NOT REACHED ({what}): no reply of THIS question was pending (new request={newRequest}, in flight={inFlight}, " +
                        $"status busy={busy} '{status}', new answer rows={newRows}). Nothing about Boo was checked.");
        }
    }

    /// <summary>Every transcript row ("You: ...", "Boo: ...", "Source ..."), oldest first.</summary>
    protected static List<string> Rows(Window panel)
    {
        var list = TryFind(panel, "TranscriptList");
        if (list is null) return [];
        try
        {
            return list.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)).Select(BooAppFixture.SafeName).ToList();
        }
        catch (Exception) { return []; }
    }

    protected static List<string> BooRows(Window panel) =>
        Rows(panel).Where(r => r.StartsWith("Boo: ", StringComparison.Ordinal)).Select(r => r["Boo: ".Length..]).ToList();

    protected static string Status(Window panel) => NameOfId(panel, "StatusText");

    protected static AutomationElement? TryFind(Window panel, string id)
    {
        try { return panel.FindFirstDescendant(cf => cf.ByAutomationId(id)); }
        catch (Exception) { return null; }
    }

    protected AutomationElement Find(Window panel, string id) =>
        Boo.WaitFor(() => TryFind(panel, id), UiTimeout, $"element '{id}'");

    protected static string NameOfId(Window panel, string id) =>
        TryFind(panel, id) is { } e ? BooAppFixture.SafeName(e) : string.Empty;

    protected static AutomationElement? Visible(AutomationElement? e)
    {
        try { return e is not null && !e.IsOffscreen && e.BoundingRectangle.Width > 0 ? e : null; }
        catch (Exception) { return null; }
    }

    protected static bool IsEnabled(AutomationElement? e)
    {
        try { return e is not null && e.IsEnabled; }
        catch (Exception) { return false; }
    }

    protected void SelectDisplay(Window panel)
    {
        try
        {
            var combo = TryFind(panel, "DisplayBox")?.AsComboBox();
            if (combo is not null && combo.SelectedItem is null && combo.Items.Length > 0) { combo.Select(0); Thread.Sleep(400); }
        }
        catch (Exception ex) { Output.WriteLine("DisplayBox: " + ex.Message); }
    }

    /// <summary>A Boo status line while a turn is still running.</summary>
    protected static bool LooksBusy(string status) =>
        status.StartsWith("Thinking", StringComparison.Ordinal) || status.Contains("waiting for the response", StringComparison.OrdinalIgnoreCase);

    protected static string Shorten(string? text, int max) =>
        text is null ? "<none>" : text.Length <= max ? text : text[..max] + "...";
}
