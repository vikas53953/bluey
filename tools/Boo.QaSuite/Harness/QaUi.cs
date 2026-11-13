using System.Diagnostics;
using System.IO;
using Boo.Core;
using Boo.Demo.Tests;
using Boo.LiveSmoke;
using Boo.Storage;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Boo.QaSuite;

/// <summary>Small UI Automation helpers shared by the suite's groups. Every read is one UIA call; nothing is cached.</summary>
public static class QaUi
{
    public static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(60);

    public static AutomationElement? TryFind(Window window, string automationId)
    {
        try
        {
            return window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static AutomationElement Find(BooAppFixture boo, Window window, string automationId) =>
        boo.WaitFor(() => TryFind(window, automationId), UiTimeout, $"element '{automationId}'");

    public static string NameOf(Window window, string automationId) =>
        TryFind(window, automationId) is { } e ? BooAppFixture.SafeName(e) : string.Empty;

    /// <summary>Every transcript row Boo wrote ("Boo: ..."), oldest first, without the prefix.</summary>
    public static IReadOnlyList<string> BooRows(Window panel)
    {
        var rows = new List<string>();
        if (TryFind(panel, "TranscriptList") is not { } list) return rows;
        try
        {
            foreach (var item in list.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)))
            {
                var name = BooAppFixture.SafeName(item);
                if (name.StartsWith("Boo: ", StringComparison.Ordinal)) rows.Add(name["Boo: ".Length..]);
            }
        }
        catch (Exception)
        {
            // The list is being rebuilt; the caller polls again.
        }
        return rows;
    }

    /// <summary>
    /// Opens the panel the way a user does (Ctrl+Alt+B), safely for a Boo that has only just started: Boo registers
    /// its hotkeys when the notch window is created, so a press before that is lost (run 43425302356: every freshly
    /// started Boo missed its first press). Waits for the notch, then presses up to 3 times. Use this, never
    /// <see cref="BooAppFixture.EnsurePanel"/>, everywhere in the suite.
    /// </summary>
    public static Window OpenPanel(BooAppFixture boo)
    {
        boo.WaitForNotch(UiTimeout);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (boo.FindPanel() is { } open) return open;
            BooAppFixture.PressOpenShortcut();
            if (BooAppFixture.WaitUntil(() => boo.FindPanel() is not null, TimeSpan.FromSeconds(8)) && boo.FindPanel() is { } panel)
                return panel;
        }
        throw new TimeoutException("Ctrl+Alt+B did not open Boo's panel after 3 presses, with the notch already up.");
    }

    /// <summary>True while a typed request is being worked on: Boo shows Capture while it takes its screenshot, which
    /// outranks Thinking for the whole request, so either face counts as "working".</summary>
    public static bool IsWorkingFace(string face) => face is "Thinking" or "Capture";

    /// <summary>Types a question into Boo's Ask box and presses Send (the same controls a user clicks).</summary>
    public static void Ask(BooAppFixture boo, Window panel, string text)
    {
        var input = Find(boo, panel, "InputBox").AsTextBox();
        input.Text = text;
        Find(boo, panel, "SendButton").AsButton().Invoke();
    }

    /// <summary>The notch's current face (its UIA ItemStatus, the NotchFaceState name), or empty.</summary>
    public static string NotchState(BooAppFixture boo)
    {
        try
        {
            return boo.FindNotch()?.Properties.ItemStatus.ValueOrDefault ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Waits until Boo's status line shows one of <paramref name="expected"/> exactly; returns every distinct status seen
    /// (in order) so a failure names what Boo said instead.
    /// </summary>
    public static (bool Matched, string Last, List<string> Seen) WaitForStatus(Window panel, IReadOnlyCollection<string> expected, TimeSpan timeout)
    {
        var seen = new List<string>();
        var matched = BooAppFixture.WaitUntil(() =>
        {
            var now = NameOf(panel, "StatusText");
            if (now.Length > 0 && (seen.Count == 0 || seen[^1] != now)) seen.Add(now);
            return expected.Contains(now);
        }, timeout);
        return (matched, NameOf(panel, "StatusText"), seen);
    }

    public static string Shorten(string? text, int max) =>
        text is null ? "<none>" : text.Length <= max ? text : text[..max] + "...";
}

/// <summary>Seeds for the error-path fixtures. None of them holds a real key.</summary>
public static class QaSeeds
{
    /// <summary>
    /// A well-formed key that OpenAI will reject (401). Random per run, and deliberately not key-shaped (no "sk-"
    /// prefix), so the evidence key scan never mistakes it for a secret. It never reaches a screenshot or a log.
    /// </summary>
    public static string FakeKey { get; } = "boo-qa-not-a-real-key-" + Guid.NewGuid().ToString("N");

    public static void SpeechOff(string dataDirectory) =>
        new SettingsStore(Path.Combine(dataDirectory, "settings.json"))
            .Save(new AppSettings(SpeechEnabled: false, Provider: new ProviderSettings(TextProvider: AiProvider.OpenAI)));

    public static void FakeKeySpeechOff(string dataDirectory)
    {
        Environment.SetEnvironmentVariable(LiveAppFixture.CaptureScopeVariable, "boo-windows");
        new CredentialStore(Path.Combine(dataDirectory, AiProviders.CredentialFileName(AiProvider.OpenAI))).Save(FakeKey);
        SpeechOff(dataDirectory);
    }

    public static void NoKeySpeechOff(string dataDirectory)
    {
        Environment.SetEnvironmentVariable(LiveAppFixture.CaptureScopeVariable, "boo-windows");
        SpeechOff(dataDirectory);
    }
}

/// <summary>Boo with a fake (rejected) OpenAI key and speech off. A fresh Boo per test class: no pooled connection carries over.</summary>
public sealed class FakeKeyFixture : BooAppFixture
{
    public FakeKeyFixture() : base(QaSeeds.FakeKeySpeechOff) { }
}

/// <summary>Boo with no key at all and speech off.</summary>
public sealed class NoKeyFixture : BooAppFixture
{
    public NoKeyFixture() : base(QaSeeds.NoKeySpeechOff) { }
}

/// <summary>
/// Records every notch face change (UIA ItemStatus) on its own thread with its own UI Automation connection, about
/// every 40 ms, appending each change to <c>qa-notch-states-&lt;label&gt;.txt</c> as it happens (so the file is complete
/// even if the run is killed). The report reads these files to list which notch states this run saw live.
/// </summary>
public sealed class NotchStateRecorder : IDisposable
{
    private readonly BooAppFixture _boo;
    private readonly string _file;
    private readonly Thread _thread;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly List<(long Start, long End, string State)> _observations = new();
    public long Mark() => _clock.ElapsedMilliseconds;
    public bool HasSeenSince(string state, long mark)
    {
        lock (_gate) return _observations.Any(o => o.Start > mark && o.State == state);
    }
    public string? ObservationFailureSince(long start)
    {
        var end = _clock.ElapsedMilliseconds;
        (long Start, long End, string State)[] observations;
        lock (_gate) observations = _observations.Where(o => o.Start > start && o.End <= end).ToArray();
        File.WriteAllLines(Path.ChangeExtension(_file, ".health.txt"),
            new[] { $"# action window [{start},{end}], maximum read/gap 100 ms" }
                .Concat(observations.Select(o => $"read [{o.Start},{o.End}] observed {o.State}")));
        if (observations.Length < 2) return "Insufficient recorder observations.";
        if (observations[0].Start > start + 100 || observations[^1].End < end - 100)
            return "Recorder did not cover both action-window boundaries within 100 ms.";
        for (var i = 0; i < observations.Length; i++)
        {
            var observation = observations[i];
            if (observation.State.Length == 0 || observation.End - observation.Start > 100)
                return $"Failed or slow face read [{observation.Start},{observation.End}].";
            if (i > 0 && observation.Start - observations[i - 1].End > 100)
                return $"Recorder observation gap before {observation.Start} exceeded 100 ms.";
        }
        return null;
    }
    private volatile bool _stop;

    public NotchStateRecorder(BooAppFixture boo, string label)
    {
        _boo = boo;
        _file = Path.Combine(boo.ArtifactsDirectory, $"qa-notch-states-{label}.txt");
        File.WriteAllText(_file, $"# notch face changes (UIA ItemStatus), label={label}, ms since recorder start{Environment.NewLine}");
        _thread = new Thread(Run) { IsBackground = true, Name = "qa-notch-recorder" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public IReadOnlyCollection<string> Seen
    {
        get { lock (_gate) return _seen.ToArray(); }
    }

    public bool HasSeen(string state)
    {
        lock (_gate) return _seen.Contains(state);
    }

    private void Run()
    {
        using var automation = new UIA3Automation();
        var last = string.Empty;
        Window? notch = null;
        while (!_stop)
        {
            var state = string.Empty;
            var readStart = _clock.ElapsedMilliseconds;
            try
            {
                notch ??= _boo.App.GetAllTopLevelWindows(automation).FirstOrDefault(w =>
                {
                    var n = BooAppFixture.SafeName(w);
                    return n == BooAppFixture.NotchName || n == BooAppFixture.NotchTitle;
                });
                state = notch?.Properties.ItemStatus.ValueOrDefault ?? string.Empty;
            }
            catch (Exception)
            {
                // A retained UIA element can go stale when Boo exits. Keep the failed read as health evidence.
                notch = null;
            }
            lock (_gate) _observations.Add((readStart, _clock.ElapsedMilliseconds, state));
            if (state.Length > 0 && state != last)
            {
                last = state;
                lock (_gate)
                {
                    _seen.Add(state);
                }
                try
                {
                    File.AppendAllText(_file, $"{_clock.ElapsedMilliseconds} {state}{Environment.NewLine}");
                }
                catch (IOException)
                {
                    // Evidence folder briefly locked; the next change is written.
                }
            }
            Thread.Sleep(40);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
