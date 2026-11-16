using System.Diagnostics;
using System.IO;
using Boo.Core;
using Boo.Demo.Tests;
using Boo.Storage;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace Boo.CrashReopen;

/// <summary>A fresh data folder, speech off, no key (no AI request can be made). One per test class.</summary>
public sealed class AckFailureFixture : BooAppFixture
{
    public AckFailureFixture() : base(FocusAckDriver.Seed) { }
}

/// <summary>A fresh data folder, speech off, no key (no AI request can be made). One per test class.</summary>
public sealed class AckOrderFixture : BooAppFixture
{
    public AckOrderFixture() : base(FocusAckDriver.Seed) { }
}

/// <summary>
/// PR #170 review P1 on the published Boo.exe: a status that promises a saved state is an acknowledgment of one save. When
/// sessions.db cannot be written (made read-only here, a permanent failure), Pause must end on an honest "Not saved: ..."
/// status, never the saved "Paused. ..." text, and the stored row must not claim Paused. Once history is unavailable
/// (no store), a later End is acknowledged as not saved too.
/// </summary>
public sealed class FocusSaveFailureAcknowledgmentTests : IClassFixture<AckFailureFixture>
{
    private readonly FocusAckDriver _boo;

    public FocusSaveFailureAcknowledgmentTests(AckFailureFixture boo, ITestOutputHelper output) => _boo = new FocusAckDriver(boo, output);

    [Fact]
    public void PauseWhoseSaveFails_SaysNotSaved_AndNeverThePausedAcknowledgment()
    {
        var db = _boo.Database;
        var task = "ack-failure " + Guid.NewGuid().ToString("N")[..8];
        try
        {
            _boo.Start(task);
            _boo.WaitStatus(s => s.StartsWith("Running;", StringComparison.Ordinal), "Running after Start");
            Assert.Equal(FocusStatus.Running, _boo.Stored(task)?.Status);

            File.SetAttributes(db, File.GetAttributes(db) | FileAttributes.ReadOnly);
            _boo.Record("sessions.db made read-only (permanent save failure)");
            _boo.Press("FocusPauseButton");
            _boo.WaitFace("Paused");
            var final = _boo.WaitStatus(s => s.StartsWith("Not saved:", StringComparison.Ordinal) || s.StartsWith("Paused.", StringComparison.Ordinal),
                "an acknowledgment of the failed Pause save");
            _boo.Stable(TimeSpan.FromSeconds(2));
            final = _boo.Status();
            _boo.Record($"after failed Pause save: status='{final}' stored={_boo.Stored(task)?.Status}");
            Assert.StartsWith("Not saved:", final, StringComparison.Ordinal);
            Assert.DoesNotContain("Paused. Resume is explicit", final, StringComparison.Ordinal);
            Assert.NotEqual(FocusStatus.Paused, _boo.Stored(task)?.Status);

            _boo.Press("FocusEndButton");
            var ended = _boo.WaitStatus(s => s.StartsWith("Not saved:", StringComparison.Ordinal) && s.Contains("ended", StringComparison.Ordinal)
                || s.StartsWith("Ended.", StringComparison.Ordinal), "an acknowledgment of End with no history store");
            _boo.Record($"End with no store: status='{ended}'");
            Assert.StartsWith("Not saved:", ended, StringComparison.Ordinal);
            _boo.Capture("ack-failure");
        }
        finally
        {
            if (File.Exists(db))
            {
                File.SetAttributes(db, File.GetAttributes(db) & ~FileAttributes.ReadOnly);
            }
            _boo.Flush("ack-failure");
        }
    }
}

/// <summary>
/// PR #170 review P2 on the published Boo.exe: acknowledgments are ordered by the action that asked for them, so the newest
/// action's status wins even when an older action's save finishes later. Saves are held back by an exclusive lock on
/// sessions.db (as a slow disk or a backup scan would), then released. Each case runs in its own fresh Boo, so one case
/// can never leave state that decides another.
/// </summary>
public sealed class StartThenPauseAcknowledgmentTests : IClassFixture<AckOrderFixture>
{
    private readonly FocusAckDriver _boo;

    public StartThenPauseAcknowledgmentTests(AckOrderFixture boo, ITestOutputHelper output) => _boo = new FocusAckDriver(boo, output);

    [Fact]
    public void StartThenPause_WithStartsWriteHeld_EndsPaused()
    {
        var task = "ack-start-pause " + Guid.NewGuid().ToString("N")[..8];
        try
        {
            _boo.EnsureNoRunningSession();
            using (_boo.HoldWrites())
            {
                _boo.Start(task);
                _boo.Press("FocusPauseButton");
                _boo.WaitFace("Paused");
            }
            _boo.WaitStatus(s => s.StartsWith("Paused.", StringComparison.Ordinal), "Paused after the held writes");
            _boo.Stable(TimeSpan.FromSeconds(2));
            Assert.StartsWith("Paused.", _boo.Status(), StringComparison.Ordinal);
            Assert.True(BooAppFixture.WaitUntil(() => _boo.Stored(task)?.Status == FocusStatus.Paused, TimeSpan.FromSeconds(20)),
                $"stored: {_boo.Stored(task)?.Status}");
        }
        finally
        {
            _boo.Flush("ack-start-pause");
        }
    }
}

public sealed class PauseThenEndAcknowledgmentTests : IClassFixture<AckOrderFixture>
{
    private readonly FocusAckDriver _boo;

    public PauseThenEndAcknowledgmentTests(AckOrderFixture boo, ITestOutputHelper output) => _boo = new FocusAckDriver(boo, output);

    [Fact]
    public void PauseThenEnd_WithPausesWriteHeld_EndsEnded()
    {
        var task = "ack-pause-end " + Guid.NewGuid().ToString("N")[..8];
        try
        {
            _boo.EnsureNoRunningSession();
            _boo.Start(task);
            _boo.WaitStatus(s => s.StartsWith("Running;", StringComparison.Ordinal), "Running after Start");
            using (_boo.HoldWrites())
            {
                _boo.Press("FocusPauseButton");
                _boo.WaitFace("Paused");
                _boo.Press("FocusEndButton");
                Thread.Sleep(300);
            }
            _boo.WaitStatus(s => s.StartsWith("Ended.", StringComparison.Ordinal), "Ended after the held writes");
            _boo.Stable(TimeSpan.FromSeconds(2));
            Assert.StartsWith("Ended.", _boo.Status(), StringComparison.Ordinal);
            Assert.True(BooAppFixture.WaitUntil(() => _boo.Stored(task)?.Status == FocusStatus.Ended, TimeSpan.FromSeconds(20)),
                $"stored: {_boo.Stored(task)?.Status}");
        }
        finally
        {
            _boo.Flush("ack-pause-end");
        }
    }
}

public sealed class PauseThenResumeAcknowledgmentTests : IClassFixture<AckOrderFixture>
{
    private readonly FocusAckDriver _boo;

    public PauseThenResumeAcknowledgmentTests(AckOrderFixture boo, ITestOutputHelper output) => _boo = new FocusAckDriver(boo, output);

    [Fact]
    public void PauseThenResume_WithPausesWriteHeld_StaysRunning()
    {
        var task = "ack-pause-resume " + Guid.NewGuid().ToString("N")[..8];
        try
        {
            _boo.EnsureNoRunningSession();
            _boo.Start(task);
            _boo.WaitStatus(s => s.StartsWith("Running;", StringComparison.Ordinal), "Running after Start");
            using (_boo.HoldWrites())
            {
                _boo.Press("FocusPauseButton");
                _boo.WaitFace("Paused");
                _boo.Press("FocusResumeButton");
                _boo.WaitStatus(s => s == "Running after explicit Resume.", "Running after Resume");
            }
            _boo.Stable(TimeSpan.FromSeconds(3));
            Assert.Equal("Running after explicit Resume.", _boo.Status());
        }
        finally
        {
            _boo.Flush("ack-pause-resume");
        }
    }
}

public sealed class StartReplacingARunningSessionAcknowledgmentTests : IClassFixture<AckOrderFixture>
{
    private readonly FocusAckDriver _boo;

    public StartReplacingARunningSessionAcknowledgmentTests(AckOrderFixture boo, ITestOutputHelper output) => _boo = new FocusAckDriver(boo, output);

    [Fact]
    public void StartReplacingARunningSession_WithWritesHeld_EndsOnTheNewSession()
    {
        var first = "ack-replace-a " + Guid.NewGuid().ToString("N")[..8];
        var second = "ack-replace-b " + Guid.NewGuid().ToString("N")[..8];
        try
        {
            _boo.EnsureNoRunningSession();
            _boo.Start(first);
            _boo.WaitStatus(s => s.StartsWith("Running;", StringComparison.Ordinal), "Running after the first Start");
            using (_boo.HoldWrites())
            {
                _boo.Start(second);
                Thread.Sleep(300);
            }
            _boo.WaitStatus(s => s.StartsWith("Running;", StringComparison.Ordinal), "Running after the replacing Start");
            Assert.True(BooAppFixture.WaitUntil(
                () => _boo.Stored(first)?.Status == FocusStatus.Ended && _boo.Stored(second)?.Status == FocusStatus.Running,
                TimeSpan.FromSeconds(20)),
                $"stored first={_boo.Stored(first)?.Status} second={_boo.Stored(second)?.Status}");
            _boo.Stable(TimeSpan.FromSeconds(2));
            Assert.StartsWith("Running;", _boo.Status(), StringComparison.Ordinal);
        }
        finally
        {
            _boo.Flush("ack-replace");
        }
    }
}

/// <summary>Drives the focus panel of the published Boo.exe through UI Automation and reads sessions.db from a copy.</summary>
public sealed class FocusAckDriver
{
    private static readonly TimeSpan Ui = TimeSpan.FromSeconds(30);
    private readonly BooAppFixture _boo;
    private readonly ITestOutputHelper _output;
    private readonly List<string> _log = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Window? _panel;

    public FocusAckDriver(BooAppFixture boo, ITestOutputHelper output)
    {
        _boo = boo;
        _output = output;
    }

    public static void Seed(string dir)
    {
        Environment.SetEnvironmentVariable("BOO_CAPTURE_SCOPE", "boo-windows");
        new SettingsStore(Path.Combine(dir, "settings.json"))
            .Save(new AppSettings(SpeechEnabled: false, Provider: new ProviderSettings(TextProvider: AiProvider.OpenAI)));
    }

    public string Database => Path.Combine(_boo.DataDirectory, "sessions.db");

    private Window Panel
    {
        get
        {
            if (_panel is null)
            {
                _boo.WaitForNotch(TimeSpan.FromSeconds(60));
                Thread.Sleep(1000);
                _panel = _boo.EnsurePanel();
                var rect = _boo.WaitForNotch(TimeSpan.FromSeconds(10)).BoundingRectangle;
                Mouse.MoveTo(new System.Drawing.Point(rect.Left + rect.Width / 2, rect.Top + 400));
            }
            return _panel;
        }
    }

    public void Record(string text)
    {
        _log.Add($"{_clock.ElapsedMilliseconds} {text}");
        _output.WriteLine(text);
    }

    public void Start(string task)
    {
        Element("FocusTaskBox").AsTextBox().Text = task;
        Element("FocusDurationBox").AsTextBox().Text = "25";
        Press("FocusStartButton");
    }

    public void Press(string id)
    {
        Element(id).AsButton().Invoke();
        Record($"pressed {id}; status='{Status()}'");
    }

    public string Status() =>
        Panel.FindFirstDescendant(cf => cf.ByAutomationId("FocusStatus")) is { } e ? BooAppFixture.SafeName(e) : string.Empty;

    public string WaitStatus(Func<string, bool> done, string what)
    {
        Assert.True(BooAppFixture.WaitUntil(() => done(Status()), Ui), $"{what} did not arrive. Status: '{Status()}'");
        return Status();
    }

    public void WaitFace(string face) =>
        Assert.True(BooAppFixture.WaitUntil(() => (_boo.FindNotch()?.Properties.ItemStatus.ValueOrDefault ?? string.Empty) == face, TimeSpan.FromSeconds(5)),
            $"the notch did not show {face}");

    /// <summary>Waits a while and fails if the status changes meanwhile (no late acknowledgment may overwrite it).</summary>
    public void Stable(TimeSpan duration)
    {
        var first = Status();
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < duration)
        {
            Assert.Equal(first, Status());
            Thread.Sleep(100);
        }
    }

    public void EnsureNoRunningSession()
    {
        if (Status().StartsWith("Running", StringComparison.Ordinal) || Status().StartsWith("Paused", StringComparison.Ordinal))
        {
            Press("FocusEndButton");
            WaitStatus(s => s.StartsWith("Ended.", StringComparison.Ordinal) || s.StartsWith("Not saved:", StringComparison.Ordinal), "End of the previous session");
        }
    }

    /// <summary>Holds an exclusive lock on sessions.db so Boo's saves wait; disposing releases it.</summary>
    public IDisposable HoldWrites()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
        connection.Open();
        using (var begin = connection.CreateCommand())
        {
            begin.CommandText = "BEGIN EXCLUSIVE;";
            begin.ExecuteNonQuery();
        }
        Record("sessions.db locked");
        return new Release(() =>
        {
            using (var rollback = connection.CreateCommand())
            {
                rollback.CommandText = "ROLLBACK;";
                rollback.ExecuteNonQuery();
            }
            connection.Dispose();
            SqliteConnection.ClearAllPools();
            Record("sessions.db unlocked");
        });
    }

    public FocusSnapshot? Stored(string task)
    {
        var copy = Path.Combine(Path.GetTempPath(), "boo-ack-read-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(copy);
            foreach (var file in Directory.EnumerateFiles(_boo.DataDirectory, "sessions.db*"))
            {
                var target = Path.Combine(copy, Path.GetFileName(file));
                File.Copy(file, target, overwrite: true);
                File.SetAttributes(target, FileAttributes.Normal);
            }
            var db = Path.Combine(copy, "sessions.db");
            var session = File.Exists(db) ? new SessionStore(db).List().FirstOrDefault(s => s.Task == task) : null;
            SqliteConnection.ClearAllPools();
            return session;
        }
        catch (IOException)
        {
            return null;
        }
        finally
        {
            try { Directory.Delete(copy, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public void Capture(string name) => _boo.CaptureScreen(name);

    public void Flush(string name)
    {
        try { _boo.WriteObservation(name, string.Join("\n", _log)); }
        catch (IOException) { }
    }

    private AutomationElement Element(string id) =>
        _boo.WaitFor(() => Panel.FindFirstDescendant(cf => cf.ByAutomationId(id)), Ui, $"element '{id}'");

    private sealed class Release(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
