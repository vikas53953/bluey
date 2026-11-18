using Boo.Core;

namespace Boo.Core.Tests;

public sealed class CrashReportTests
{
    private static readonly DateTimeOffset When = new(2026, 11, 8, 21, 5, 9, 123, TimeSpan.Zero);

    [Theory]
    [InlineData("key sk-abcdefghijklmnopqrstuvwxyz0123456789 failed", "key [redacted-key] failed")]
    [InlineData("sk-proj-AbC_def-1234567890abcdefghij", "[redacted-key]")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.abc.def done", "Authorization: Bearer [redacted] done")]
    [InlineData("bearer abcdef123456", "bearer [redacted]")]
    [InlineData("api_key=abcd1234efgh&x=1", "api_key=[redacted]&x=1")]
    [InlineData("\"apiKey\": \"abcd1234efgh\"", "\"apiKey\": \"[redacted]\"")]
    public void RedactRemovesSecrets(string input, string expected)
    {
        Assert.Equal(expected, CrashReport.Redact(input));
    }

    [Fact]
    public void RedactLeavesOrdinaryTextAlone()
    {
        const string text = "System.InvalidOperationException: The task skipped a frame at Boo.App.MainWindow.Ask()";
        Assert.Equal(text, CrashReport.Redact(text));
    }

    [Fact]
    public void RedactReplacesUserProfilePath()
    {
        var text = @"at Boo.App.X() in C:\Users\vikas\src\Boo\App.cs:line 4";
        Assert.Equal(@"at Boo.App.X() in %USERPROFILE%\src\Boo\App.cs:line 4",
            CrashReport.Redact(text, @"C:\Users\vikas"));
    }

    [Fact]
    public void RedactHandlesNullAndEmpty()
    {
        Assert.Equal(string.Empty, CrashReport.Redact(null));
        Assert.Equal(string.Empty, CrashReport.Redact(string.Empty));
    }

    [Fact]
    public void FileNameIsSortableAndFileSystemSafe()
    {
        var name = CrashReport.FileNameFor(When);
        Assert.Equal("crash-20261108-210509-123.txt", name);
        Assert.True(CrashReport.TryParseTimestamp(name, out var parsed));
        Assert.Equal(When, parsed);
    }

    [Fact]
    public void FileNameUsesUtc()
    {
        var local = new DateTimeOffset(2026, 11, 9, 2, 35, 9, 123, TimeSpan.FromHours(5.5));
        Assert.Equal("crash-20261108-210509-123.txt", CrashReport.FileNameFor(local));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("crash-2026.txt")]
    [InlineData("crash-20261399-210509-123.txt")]
    [InlineData("")]
    public void TryParseRejectsOtherFiles(string name)
    {
        Assert.False(CrashReport.TryParseTimestamp(name, out _));
    }

    [Fact]
    public void FilesToDeleteKeepsNewest()
    {
        var names = new[]
        {
            "crash-20261101-000000-000.txt",
            "crash-20261105-000000-000.txt",
            "unrelated.txt",
            "crash-20261103-000000-000.txt",
            "crash-20261107-000000-000.txt"
        };

        var doomed = CrashReport.FilesToDelete(names, keep: 2);

        Assert.Equal(new[] { "crash-20261101-000000-000.txt", "crash-20261103-000000-000.txt" }, doomed);
    }

    [Fact]
    public void FilesToDeleteWithFewFilesDeletesNothing()
    {
        Assert.Empty(CrashReport.FilesToDelete(new[] { "crash-20261101-000000-000.txt" }, keep: 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => CrashReport.FilesToDelete(Array.Empty<string>(), keep: 0));
    }

    [Fact]
    public void FormatContainsSourceTimeAndRedactedDetails()
    {
        var ex = new InvalidOperationException("Request with Bearer abc123def456 failed");
        var text = CrashReport.Format(ex, "Dispatcher", When, recovered: true, appVersion: "1.2.3");

        Assert.Contains("Boo crash report", text);
        Assert.Contains("Source: Dispatcher", text);
        Assert.Contains("Recovered: yes", text);
        Assert.Contains("Version: 1.2.3", text);
        Assert.Contains("2026-11-08T21:05:09", text);
        Assert.Contains("System.InvalidOperationException", text);
        Assert.DoesNotContain("abc123def456", text);
    }

    [Fact]
    public void FormatIncludesInnerExceptions()
    {
        var ex = new AggregateException(new TimeoutException("inner sk-abcdefghijklmnopqrstuvwxyz0123"));
        var text = CrashReport.Format(ex, "TaskScheduler", When, recovered: true);

        Assert.Contains("System.TimeoutException", text);
        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz0123", text);
    }

    [Fact]
    public void FormatAcceptsNonExceptionObjects()
    {
        var text = CrashReport.Format("weird thing", "AppDomain", When, recovered: false);
        Assert.Contains("weird thing", text);
        Assert.Contains("Recovered: no", text);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException), true)]
    [InlineData(typeof(NullReferenceException), true)]
    [InlineData(typeof(OutOfMemoryException), false)]
    [InlineData(typeof(InsufficientExecutionStackException), false)]
    [InlineData(typeof(AccessViolationException), false)]
    [InlineData(typeof(BadImageFormatException), false)]
    public void RecoverabilityByExceptionType(Type type, bool recoverable)
    {
        var ex = (Exception)Activator.CreateInstance(type)!;
        Assert.Equal(recoverable, CrashReport.IsRecoverable(ex));
    }

    [Fact]
    public void FatalInnerExceptionIsNotRecoverable()
    {
        Assert.False(CrashReport.IsRecoverable(new InvalidOperationException("x", new OutOfMemoryException())));
    }

    [Fact]
    public void RecentCrashIsDetectedWithinWindow()
    {
        var names = new[] { "crash-20261108-200000-000.txt", "crash-20261101-000000-000.txt", "readme.txt" };
        var now = new DateTimeOffset(2026, 11, 8, 21, 0, 0, TimeSpan.Zero);

        Assert.True(CrashReport.HasRecentCrash(names, now, TimeSpan.FromHours(2)));
        Assert.False(CrashReport.HasRecentCrash(names, now, TimeSpan.FromMinutes(30)));
        Assert.False(CrashReport.HasRecentCrash(Array.Empty<string>(), now, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void NotifiesOnlyForCrashNewerThanLastNotice()
    {
        var names = new[] { "crash-20261108-200000-000.txt", "crash-20261101-000000-000.txt" };
        var now = new DateTimeOffset(2026, 11, 8, 21, 0, 0, TimeSpan.Zero);
        var window = TimeSpan.FromDays(7);

        Assert.True(CrashReport.ShouldNotify(names, lastNotified: null, now, window));
        Assert.True(CrashReport.ShouldNotify(names, new DateTimeOffset(2026, 11, 8, 19, 0, 0, TimeSpan.Zero), now, window));
        Assert.False(CrashReport.ShouldNotify(names, new DateTimeOffset(2026, 11, 8, 20, 30, 0, TimeSpan.Zero), now, window));
        Assert.False(CrashReport.ShouldNotify(names, lastNotified: null, now.AddDays(10), window));
        Assert.False(CrashReport.ShouldNotify(new[] { "other.txt" }, lastNotified: null, now, window));
    }

    [Fact]
    public void NewestCrashIgnoresOtherFiles()
    {
        Assert.Null(CrashReport.NewestCrash(new[] { "x.txt" }));
        Assert.Equal(new DateTimeOffset(2026, 11, 8, 20, 0, 0, TimeSpan.Zero),
            CrashReport.NewestCrash(new[] { "crash-20261101-000000-000.txt", "crash-20261108-200000-000.txt", "y" }));
    }

    [Fact]
    public void NoticesArePlain()
    {
        Assert.Equal("Boo recovered from a problem. Details are in the logs folder.", CrashReport.RecoveredNotice);
        Assert.Equal("Boo hit a problem and stopped what it was doing. Details are in the logs folder.", CrashReport.KeptRunningNotice);
    }
}

public sealed class CrashLoopGuardTests
{
    [Fact]
    public void AllowsRecoveryUntilBurstLimit()
    {
        var start = new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
        var guard = new CrashLoopGuard(maxRecoveries: 3, window: TimeSpan.FromMinutes(1));

        Assert.True(guard.TryRecover(start));
        Assert.True(guard.TryRecover(start.AddSeconds(1)));
        Assert.True(guard.TryRecover(start.AddSeconds(2)));
        Assert.False(guard.TryRecover(start.AddSeconds(3)));
    }

    [Fact]
    public void OldFailuresAgeOut()
    {
        var start = new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
        var guard = new CrashLoopGuard(maxRecoveries: 2, window: TimeSpan.FromMinutes(1));

        Assert.True(guard.TryRecover(start));
        Assert.True(guard.TryRecover(start.AddSeconds(10)));
        Assert.True(guard.TryRecover(start.AddMinutes(2)));
    }

    [Fact]
    public void RejectsInvalidConfiguration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CrashLoopGuard(0, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CrashLoopGuard(1, TimeSpan.Zero));
    }
}
