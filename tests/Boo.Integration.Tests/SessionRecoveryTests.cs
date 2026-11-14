using Boo.Core;
using Boo.Storage;

namespace Boo.Integration.Tests;

public sealed class SessionRecoveryTests
{
    private static FocusSnapshot Snapshot(Guid id, double elapsed, FocusStatus status, DateTimeOffset startedAt) => new(
        id, "write report", 1500, startedAt, elapsed, elapsed, status, false, false,
        new[] { new FocusCheck(Math.Max(0, elapsed - 30), FocusActivity.OnTask, 0.9, 0.8) });

    [Fact]
    public void Running_session_left_by_a_crash_is_recovered_as_paused_on_next_launch()
    {
        var path = Path.Combine(Path.GetTempPath(), $"boo-recovery-{Guid.NewGuid():N}.sqlite");
        try
        {
            var crashedId = Guid.NewGuid();
            var endedId = Guid.NewGuid();
            var start = DateTimeOffset.Parse("2026-11-08T10:00:00Z");
            var store = new SessionStore(path);
            store.Save(Snapshot(crashedId, 420, FocusStatus.Running, start.AddHours(1)));
            store.Save(Snapshot(endedId, 1500, FocusStatus.Ended, start));

            // Next launch.
            var relaunched = new SessionStore(path);
            var recovered = relaunched.RecoverInterrupted();
            var sessions = relaunched.List();

            Assert.Equal(1, recovered);
            var crashed = Assert.Single(sessions, s => s.Id == crashedId);
            Assert.Equal(FocusStatus.Paused, crashed.Status);
            Assert.Equal(420, crashed.Elapsed);
            Assert.Equal(420, crashed.RealElapsed);
            Assert.Equal(FocusActivity.Paused, crashed.Checks[^1].Activity);
            var ended = Assert.Single(sessions, s => s.Id == endedId);
            Assert.Equal(FocusStatus.Ended, ended.Status);
            Assert.Single(ended.Checks);

            Assert.Equal(0, relaunched.RecoverInterrupted());
            Assert.Equal(2, Assert.Single(relaunched.List(), s => s.Id == crashedId).Checks.Count);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Recovery_on_an_empty_store_does_nothing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"boo-recovery-{Guid.NewGuid():N}.sqlite");
        try
        {
            var store = new SessionStore(path);
            Assert.Equal(0, store.RecoverInterrupted());
            Assert.Empty(store.List());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
