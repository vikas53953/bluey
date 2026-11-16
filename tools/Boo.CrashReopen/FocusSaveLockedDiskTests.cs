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

/// <summary>A fresh data folder, speech off, no key (no AI request can be made).</summary>
public sealed class LockedDiskFixture : BooAppFixture
{
    public LockedDiskFixture() : base(dir =>
    {
        Environment.SetEnvironmentVariable("BOO_CAPTURE_SCOPE", "boo-windows");
        new SettingsStore(Path.Combine(dir, "settings.json"))
            .Save(new AppSettings(SpeechEnabled: false, Provider: new ProviderSettings(TextProvider: AiProvider.OpenAI)));
    }) { }
}

/// <summary>
/// Issue #168 on the published Boo.exe: a slow or locked disk must never freeze Boo. The test holds an exclusive SQLite
/// lock on Boo's own sessions.db (as a backup tool, an antivirus scan or a slow disk can) while the user presses Pause.
/// While the lock is held: the notch shows Paused within a second and every UI Automation read of the face answers
/// within the unchanged 100 ms observation budget (Boo's UI thread is free), and the "Paused ..." status, which promises
/// a saved state, is not shown yet. When the lock is released, the save lands: the status says Paused and the stored
/// session is Paused. A build that writes focus history on the UI thread blocks there until the lock is released, so
/// the face reads stall for the whole lock and the test fails.
/// </summary>
public sealed class FocusSaveLockedDiskTests : IClassFixture<LockedDiskFixture>
{
    private const int ReadBudgetMs = 100;
    private static readonly TimeSpan Ui = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LockHeld = TimeSpan.FromSeconds(3);
    private readonly LockedDiskFixture _boo;
    private readonly ITestOutputHelper _output;

    public FocusSaveLockedDiskTests(LockedDiskFixture boo, ITestOutputHelper output)
    {
        _boo = boo;
        _output = output;
    }

    [Fact]
    public void PauseWhileTheHistoryFileIsLocked_FaceAnswersAtOnce_AndTheSaveLandsAfterTheLock()
    {
        var log = new List<string>();
        var clock = Stopwatch.StartNew();
        void Record(string text)
        {
            log.Add($"{clock.ElapsedMilliseconds} {text}");
            _output.WriteLine(text);
        }

        SqliteConnection? holder = null;
        try
        {
            _boo.WaitForNotch(TimeSpan.FromSeconds(60));
            Thread.Sleep(1000);
            var panel = _boo.EnsurePanel();
            var notchRect = _boo.WaitForNotch(TimeSpan.FromSeconds(10)).BoundingRectangle;
            Mouse.MoveTo(new System.Drawing.Point(notchRect.Left + notchRect.Width / 2, notchRect.Top + 400));

            const string task = "Locked-disk pause check";
            Element(panel, "FocusTaskBox").AsTextBox().Text = task;
            Element(panel, "FocusDurationBox").AsTextBox().Text = "25";
            Element(panel, "FocusStartButton").AsButton().Invoke();
            Assert.True(BooAppFixture.WaitUntil(() => Name(panel, "FocusStatus").StartsWith("Running", StringComparison.Ordinal), Ui),
                $"precondition: the session did not start. Status: '{Name(panel, "FocusStatus")}'");
            var database = Path.Combine(_boo.DataDirectory, "sessions.db");
            Assert.True(BooAppFixture.WaitUntil(() => File.Exists(database), Ui), "precondition: sessions.db was not created");
            var notch = _boo.WaitForNotch(TimeSpan.FromSeconds(10));
            Assert.True(BooAppFixture.WaitUntil(() => TimedFace(notch).Face == "Hidden", Ui), "precondition: the notch did not rest");
            var statusBefore = Name(panel, "FocusStatus");
            Record($"running: status='{statusBefore}'");

            holder = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
            holder.Open();
            using (var begin = holder.CreateCommand())
            {
                begin.CommandText = "BEGIN EXCLUSIVE;";
                begin.ExecuteNonQuery();
            }
            Record("sessions.db locked (BEGIN EXCLUSIVE)");

            var pauseButton = Element(panel, "FocusPauseButton").AsButton();
            var invoke = Task.Run(() => pauseButton.Invoke());
            var lockClock = Stopwatch.StartNew();
            var slowest = 0L;
            var sawPaused = -1L;
            var reads = 0;
            while (lockClock.Elapsed < LockHeld - TimeSpan.FromMilliseconds(300))
            {
                var (face, ms) = TimedFace(notch);
                reads++;
                slowest = Math.Max(slowest, ms);
                if (face == "Paused" && sawPaused < 0)
                {
                    sawPaused = lockClock.ElapsedMilliseconds;
                }
                Thread.Sleep(40);
            }
            var statusRead = Task.Run(() => Name(panel, "FocusStatus"));
            var statusWhileLocked = statusRead.Wait(TimeSpan.FromSeconds(2)) ? statusRead.Result : "<no answer while locked>";
            Record($"while locked: reads={reads} slowest={slowest}ms pausedAt={sawPaused}ms status='{statusWhileLocked}'");

            using (var rollback = holder.CreateCommand())
            {
                rollback.CommandText = "ROLLBACK;";
                rollback.ExecuteNonQuery();
            }
            holder.Dispose();
            holder = null;
            SqliteConnection.ClearAllPools();
            Record("sessions.db unlocked");
            invoke.Wait(Ui);

            Assert.True(BooAppFixture.WaitUntil(() => Name(panel, "FocusStatus").StartsWith("Paused", StringComparison.Ordinal), Ui),
                $"the Pause status never arrived after the lock was released. Status: '{Name(panel, "FocusStatus")}'");
            FocusSnapshot? stored = null;
            Assert.True(BooAppFixture.WaitUntil(() => (stored = ReadSession(task)) is { Status: FocusStatus.Paused }, Ui),
                $"the stored session is not Paused after the lock was released: {stored?.Status.ToString() ?? "<none>"}");
            Record($"after unlock: status='{Name(panel, "FocusStatus")}' stored={stored!.Status}");
            _boo.CaptureScreen("locked-disk-paused");

            Assert.True(slowest <= ReadBudgetMs,
                $"a face read took {slowest} ms while sessions.db was locked (budget {ReadBudgetMs} ms): Boo's UI thread waited on the disk.");
            Assert.True(sawPaused >= 0 && sawPaused <= 1000,
                $"the notch did not show Paused within a second while sessions.db was locked (pausedAt={sawPaused} ms).");
            Assert.False(statusWhileLocked.StartsWith("Paused", StringComparison.Ordinal),
                $"the status promised a saved Pause while the write could not happen: '{statusWhileLocked}'");
        }
        finally
        {
            holder?.Dispose();
            SqliteConnection.ClearAllPools();
            try { _boo.WriteObservation("locked-disk-pause", string.Join("\n", log)); }
            catch (IOException) { }
        }
    }

    /// <summary>Reads the notch face through UI Automation, bounded: a read Boo cannot answer counts as its wait.</summary>
    private static (string Face, long Ms) TimedFace(AutomationElement notch)
    {
        var watch = Stopwatch.StartNew();
        var read = Task.Run(() => notch.Properties.ItemStatus.ValueOrDefault ?? string.Empty);
        if (!read.Wait(TimeSpan.FromSeconds(10)))
        {
            return ("<no answer>", watch.ElapsedMilliseconds);
        }
        return (read.Result, watch.ElapsedMilliseconds);
    }

    private FocusSnapshot? ReadSession(string task)
    {
        var copy = Path.Combine(Path.GetTempPath(), "boo-locked-disk-read-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(copy);
            foreach (var file in Directory.EnumerateFiles(_boo.DataDirectory, "sessions.db*"))
            {
                File.Copy(file, Path.Combine(copy, Path.GetFileName(file)), overwrite: true);
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

    private AutomationElement Element(Window panel, string id) =>
        _boo.WaitFor(() => panel.FindFirstDescendant(cf => cf.ByAutomationId(id)), Ui, $"element '{id}'");

    private static string Name(Window panel, string id) =>
        panel.FindFirstDescendant(cf => cf.ByAutomationId(id)) is { } e ? BooAppFixture.SafeName(e) : string.Empty;
}
