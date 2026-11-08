using System.Text;
using Boo.Core;
using Boo.Storage;

namespace Boo.Integration.Tests;

public sealed class StoragePrivacyTests
{
    [Fact]
    public void Session_round_trips_across_store_restart_with_timeline_values()
    {
        var path = TemporaryPath(".sqlite");
        try
        {
            var id = Guid.NewGuid();
            var expected = Snapshot(id, elapsed: 180, status: FocusStatus.Ended);

            new SessionStore(path).Save(expected);
            var restored = Assert.Single(new SessionStore(path).List());

            AssertSnapshot(expected, restored);
        }
        finally
        {
            DeleteTemporary(path);
        }
    }

    [Fact]
    public void Saving_the_same_session_id_updates_in_place()
    {
        var path = TemporaryPath(".sqlite");
        try
        {
            var id = Guid.NewGuid();
            var first = Snapshot(id, elapsed: 30, status: FocusStatus.Running);
            var changed = Snapshot(id, elapsed: 90, status: FocusStatus.Paused);

            var store = new SessionStore(path);
            store.Save(first);
            store.Save(changed);

            var saved = Assert.Single(store.List());
            AssertSnapshot(changed, saved);
        }
        finally
        {
            DeleteTemporary(path);
        }
    }

    [Fact]
    public void Deleting_one_history_entry_keeps_other_entries()
    {
        var path = TemporaryPath(".sqlite");
        try
        {
            var deleted = Snapshot(Guid.NewGuid(), elapsed: 60, status: FocusStatus.Ended);
            var retained = Snapshot(Guid.NewGuid(), elapsed: 120, status: FocusStatus.Ended);
            var store = new SessionStore(path);
            store.Save(deleted);
            store.Save(retained);

            store.Delete(deleted.Id);

            var saved = Assert.Single(store.List());
            AssertSnapshot(retained, saved);
        }
        finally
        {
            DeleteTemporary(path);
        }
    }

    [Fact]
    public void Settings_round_trip_all_user_and_provider_fields()
    {
        var path = TemporaryPath(".json");
        try
        {
            var expected = new AppSettings(
                DisplayId: "DISPLAY-2",
                SpeechEnabled: false,
                ChirpsEnabled: true,
                ReducedMotion: true,
                VerticalOffset: 12.5,
                MicrophoneDevice: 7,
                FileRoot: Path.Combine(Path.GetTempPath(), "boo-fixture-root"),
                Provider: new ProviderSettings("reasoning-fixture", "realtime-fixture", "focus-fixture", "voice-fixture"));

            new SettingsStore(path).Save(expected);
            var restored = new SettingsStore(path).Load();

            Assert.Equal(expected, restored);
        }
        finally
        {
            DeleteTemporary(path);
        }
    }

    [Fact]
    public void Credential_round_trip_is_protected_at_rest_without_a_real_key()
    {
        var path = TemporaryPath(".credential");
        const string syntheticCredential = "synthetic-credential-for-tests-only";
        try
        {
            new CredentialStore(path).Save(syntheticCredential);

            var onDisk = File.ReadAllBytes(path);
            Assert.NotEmpty(onDisk);
            Assert.DoesNotContain(syntheticCredential, Encoding.UTF8.GetString(onDisk));
            Assert.Equal(syntheticCredential, new CredentialStore(path).Load());
        }
        finally
        {
            DeleteTemporary(path);
        }
    }

    [Fact]
    public void Deleting_credentials_removes_the_value()
    {
        var path = TemporaryPath(".credential");
        try
        {
            var store = new CredentialStore(path);
            store.Save("synthetic-delete-me");
            store.Delete();

            Assert.Null(store.Load());
        }
        finally
        {
            DeleteTemporary(path);
        }
    }

    private static FocusSnapshot Snapshot(Guid id, double elapsed, FocusStatus status) =>
        new(
            id,
            "write the report",
            300,
            new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero),
            elapsed,
            elapsed / 2,
            status,
            DeveloperMode: true,
            UsedDeveloperMode: true,
            Checks: new FocusCheck[]
            {
                new(30, FocusActivity.OnTask, 0.90, 0.80,
                    new Dictionary<string, double> { ["on_task"] = 0.90, ["off_task"] = 0.05, ["uncertain"] = 0.05 },
                    SampleDuration: 30),
                new(60, FocusActivity.Uncertain, 0.05, 0.40,
                    new Dictionary<string, double> { ["on_task"] = 0.40, ["off_task"] = 0.05, ["uncertain"] = 0.55 },
                    SampleDuration: 30),
                new(elapsed, FocusActivity.Paused)
            });

    private static void AssertSnapshot(FocusSnapshot expected, FocusSnapshot actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Task, actual.Task);
        Assert.Equal(expected.Duration, actual.Duration);
        Assert.Equal(expected.StartedAt, actual.StartedAt);
        Assert.Equal(expected.Elapsed, actual.Elapsed);
        Assert.Equal(expected.RealElapsed, actual.RealElapsed);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.DeveloperMode, actual.DeveloperMode);
        Assert.Equal(expected.UsedDeveloperMode, actual.UsedDeveloperMode);
        Assert.Equal(expected.Checks.Count, actual.Checks.Count);
        for (var i = 0; i < expected.Checks.Count; i++)
        {
            var left = expected.Checks[i];
            var right = actual.Checks[i];
            Assert.Equal(left.Elapsed, right.Elapsed);
            Assert.Equal(left.Activity, right.Activity);
            Assert.Equal(left.Probability, right.Probability);
            Assert.Equal(left.Confidence, right.Confidence);
            Assert.Equal(left.SampleDuration, right.SampleDuration);
            Assert.Equal(left.Nudged, right.Nudged);
            if (left.Probabilities is null)
            {
                Assert.Null(right.Probabilities);
            }
            else
            {
                Assert.NotNull(right.Probabilities);
                Assert.Equal(left.Probabilities.Count, right.Probabilities!.Count);
                foreach (var pair in left.Probabilities)
                    Assert.Equal(pair.Value, right.Probabilities[pair.Key]);
            }
        }
    }

    private static string TemporaryPath(string extension) =>
        Path.Combine(Path.GetTempPath(), $"boo-storage-{Guid.NewGuid():N}{extension}");

    private static void DeleteTemporary(string path)
    {
        foreach (var candidate in new[] { path, $"{path}-wal", $"{path}-shm" })
        {
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
