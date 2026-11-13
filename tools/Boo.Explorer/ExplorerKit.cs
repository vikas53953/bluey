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
/// The run's hard budget of REAL (billed) OpenAI requests. Every user action that makes Boo send one request is
/// spent here BEFORE the action; past the cap the action is refused (the step fails "BUDGET", it never runs).
/// Boo sends exactly one HTTP request per typed, non-undo turn on the text path (MainWindow.SendButton_OnClick ->
/// ResponsesClient.RespondAsync, one SendAsync, no retry anywhere in src/Boo.Providers) and speech is off in every
/// explorer launch, so no voice request exists. The explorer itself never retries.
/// Unbilled attempts (wrong key -> 401, firewall-blocked) are listed separately and never count as billed.
/// </summary>
public static class ExplorerLedger
{
    public const int HardCap = 20;
    private static readonly object Gate = new();
    private static readonly List<string> Billed = new();
    private static readonly List<string> Unbilled = new();

    public static int BilledCount { get { lock (Gate) return Billed.Count; } }

    public static void SpendBilled(string what)
    {
        lock (Gate)
        {
            if (Billed.Count >= HardCap)
            {
                Write();
                throw new InvalidOperationException($"BUDGET: refusing '{what}': the run already used {Billed.Count} of {HardCap} real OpenAI requests.");
            }
            Billed.Add($"{DateTime.UtcNow:HH:mm:ss} {what}");
            Write();
        }
    }

    public static void NoteUnbilled(string what)
    {
        lock (Gate) { Unbilled.Add($"{DateTime.UtcNow:HH:mm:ss} {what}"); Write(); }
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
        var sb = new StringBuilder();
        sb.AppendLine($"real (billed) OpenAI requests: {Billed.Count} of hard cap {HardCap}; retries: 0 (none exist)");
        Billed.ForEach(b => sb.AppendLine("  billed   " + b));
        sb.AppendLine($"unbilled attempts (wrong key 401 / firewall-blocked): {Unbilled.Count}");
        Unbilled.ForEach(u => sb.AppendLine("  unbilled " + u));
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
        return new Random(HashCode.Combine(seed, step.Length, step[0], step[^1]));
    }

    /// <summary>
    /// Runs a step: records the face timeline, then checks the health invariants even when the body passed. A failure
    /// leaves a -FAILED screenshot, what Boo showed, and the timeline.
    /// </summary>
    protected void Explore(string step, Action<FaceSampler> body)
    {
        using var faces = new FaceSampler(Boo);
        try
        {
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

    /// <summary>Types and sends one question. A billed ask spends the ledger first (refused past the cap).</summary>
    protected void Ask(Window panel, string text, bool billed, string label)
    {
        if (billed) ExplorerLedger.SpendBilled(label + ": " + text);
        else ExplorerLedger.NoteUnbilled(label + ": " + text);
        Type(panel, text);
        Find(panel, "SendButton").AsButton().Invoke();
        Output.WriteLine($"sent ({(billed ? "billed" : "unbilled")}): {text}");
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
