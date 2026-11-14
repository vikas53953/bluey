using Boo.Core;
using Boo.Storage;

namespace Boo.Integration.Tests;

public sealed class CrashLogStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "boo-crash-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void WritesRedactedReportUnderLogsFolder()
    {
        var store = new CrashLogStore(_root);
        var when = new DateTimeOffset(2026, 11, 8, 21, 0, 0, TimeSpan.Zero);

        var path = store.Write(new InvalidOperationException("Bearer abcdef123456 sk-abcdefghijklmnopqrstuvwxyz"), "Dispatcher", when, recovered: true);

        Assert.NotNull(path);
        Assert.Equal(Path.Combine(_root, "logs"), Path.GetDirectoryName(path));
        var text = File.ReadAllText(path!);
        Assert.Contains("Source: Dispatcher", text);
        Assert.DoesNotContain("abcdef123456", text);
        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz", text);
        Assert.True(store.HasRecentCrash(when.AddMinutes(5), TimeSpan.FromHours(1)));
    }

    [Fact]
    public void KeepsOnlyNewestReports()
    {
        var store = new CrashLogStore(_root, keep: 3);
        var start = new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
        Directory.CreateDirectory(store.LogsDirectory);
        File.WriteAllText(Path.Combine(store.LogsDirectory, "notes.txt"), "keep me");
        for (var i = 0; i < 6; i++)
        {
            Assert.NotNull(store.Write(new Exception("x"), "Test", start.AddMinutes(i), recovered: true));
        }

        Assert.True(File.Exists(Path.Combine(store.LogsDirectory, "notes.txt")));
        var names = Directory.GetFiles(store.LogsDirectory, "crash-*").Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(new[]
        {
            CrashReport.FileNameFor(start.AddMinutes(3)),
            CrashReport.FileNameFor(start.AddMinutes(4)),
            CrashReport.FileNameFor(start.AddMinutes(5))
        }, names);
    }

    [Fact]
    public void SameTimestampDoesNotOverwrite()
    {
        var store = new CrashLogStore(_root);
        var when = new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);

        var first = store.Write(new Exception("a"), "Test", when, recovered: true);
        var second = store.Write(new Exception("b"), "Test", when, recovered: true);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void RecoveredNoticeIsShownOncePerCrash()
    {
        var store = new CrashLogStore(_root);
        var when = new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
        store.Write(new Exception("a"), "Test", when, recovered: false);

        Assert.True(store.ConsumeRecoveredNotice(when.AddMinutes(1), TimeSpan.FromDays(7)));
        Assert.False(store.ConsumeRecoveredNotice(when.AddMinutes(2), TimeSpan.FromDays(7)));

        store.Write(new Exception("b"), "Test", when.AddMinutes(3), recovered: false);
        Assert.True(store.ConsumeRecoveredNotice(when.AddMinutes(4), TimeSpan.FromDays(7)));
    }

    [Fact]
    public void ErrorsBooSurvivedDoNotTriggerTheNextLaunchNotice()
    {
        var store = new CrashLogStore(_root);
        var when = new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
        store.Write(new Exception("background"), "Task", when, recovered: true);

        Assert.False(store.ConsumeRecoveredNotice(when.AddMinutes(1), TimeSpan.FromDays(7)));

        store.Write(new Exception("fatal"), "Dispatcher", when.AddMinutes(2), recovered: false);
        Assert.True(store.ConsumeRecoveredNotice(when.AddMinutes(3), TimeSpan.FromDays(7)));
    }

    [Fact]
    public void MissingFolderMeansNoRecentCrash()
    {
        Assert.False(new CrashLogStore(_root).HasRecentCrash(DateTimeOffset.UtcNow, TimeSpan.FromDays(1)));
    }
}
