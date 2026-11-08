using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Boo.Core;

/// <summary>
/// Pure crash-log policy: secret redaction, sortable file names, retention, recoverability and the plain notices
/// shown to the user. The App layer only wires the unhandled-exception events; Boo.Storage writes the file.
/// </summary>
public static class CrashReport
{
    public const string FilePrefix = "crash-";
    public const string FileExtension = ".txt";
    public const string LogsFolderName = "logs";
    public const int DefaultKeep = 10;
    public const string RecoveredNotice = "Boo recovered from a problem. Details are in the logs folder.";
    public const string KeptRunningNotice = "Boo hit a problem and stopped what it was doing. Details are in the logs folder.";

    private const string TimestampFormat = "yyyyMMdd-HHmmss-fff";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Regex OpenAiKey = new(
        @"\bsk-[A-Za-z0-9_\-]{16,}",
        RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex BearerToken = new(
        @"(?<prefix>\bbearer\s+)[A-Za-z0-9\-._~+/=]{6,}",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout);

    private static readonly Regex KeyValueSecret = new(
        @"(?<prefix>\b(?:api[_\-]?key|access[_\-]?token|client[_\-]?secret|password|secret|token)""?\s*[:=]\s*""?)[^\s""&,;]{4,}",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout);

    /// <summary>Removes API keys, bearer tokens, key=value secrets and (optionally) the user's profile path.</summary>
    public static string Redact(string? text, string? userProfilePath = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string result;
        try
        {
            result = OpenAiKey.Replace(text, "[redacted-key]");
            result = BearerToken.Replace(result, m => m.Groups["prefix"].Value + "[redacted]");
            result = KeyValueSecret.Replace(result, m => m.Groups["prefix"].Value + "[redacted]");
        }
        catch (RegexMatchTimeoutException)
        {
            // Never write unredacted text: if the redactor cannot finish, drop the details entirely.
            return "[details withheld: redaction timed out]";
        }

        if (!string.IsNullOrWhiteSpace(userProfilePath) && userProfilePath.Trim().Length > 3)
        {
            result = result.Replace(userProfilePath.TrimEnd('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    public static string FileNameFor(DateTimeOffset timestamp) =>
        FilePrefix + timestamp.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture) + FileExtension;

    public static bool TryParseTimestamp(string? fileName, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        var name = Path.GetFileName(fileName);
        if (!name.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stamp = name[FilePrefix.Length..^FileExtension.Length];
        if (!DateTime.TryParseExact(stamp, TimestampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return false;
        }

        timestamp = new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc));
        return true;
    }

    /// <summary>Crash logs beyond the newest <paramref name="keep"/>, oldest first. Unrelated files are never returned.</summary>
    public static IReadOnlyList<string> FilesToDelete(IEnumerable<string> fileNames, int keep = DefaultKeep)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keep, 1);
        ArgumentNullException.ThrowIfNull(fileNames);

        var crashes = fileNames
            .Select(name => (name, ok: TryParseTimestamp(name, out var ts), ts))
            .Where(x => x.ok)
            .OrderByDescending(x => x.ts)
            .ToList();

        return crashes.Skip(keep).OrderBy(x => x.ts).Select(x => x.name).ToArray();
    }

    /// <summary>True when a crash log was written within <paramref name="window"/> before <paramref name="now"/>.</summary>
    public static bool HasRecentCrash(IEnumerable<string> fileNames, DateTimeOffset now, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        foreach (var name in fileNames)
        {
            if (TryParseTimestamp(name, out var ts) && ts <= now && now - ts <= window)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Timestamp of the newest crash log among <paramref name="fileNames"/>, or null.</summary>
    public static DateTimeOffset? NewestCrash(IEnumerable<string> fileNames)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        DateTimeOffset? newest = null;
        foreach (var name in fileNames)
        {
            if (TryParseTimestamp(name, out var ts) && (newest is null || ts > newest))
            {
                newest = ts;
            }
        }

        return newest;
    }

    /// <summary>
    /// True when the newest crash log is within <paramref name="window"/> of <paramref name="now"/> and newer than the
    /// last time the user was told about one, so the "recovered" notice appears once per new crash.
    /// </summary>
    public static bool ShouldNotify(IEnumerable<string> fileNames, DateTimeOffset? lastNotified, DateTimeOffset now, TimeSpan window)
    {
        var newest = NewestCrash(fileNames);
        return newest is { } ts
            && ts <= now
            && now - ts <= window
            && (lastNotified is null || ts > lastNotified);
    }

    /// <summary>
    /// Whether the process can keep running after this exception. Memory/stack exhaustion, corrupted state and
    /// broken binaries are fatal; ordinary bugs on the UI thread are survivable.
    /// </summary>
    public static bool IsRecoverable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is OutOfMemoryException
                or InsufficientExecutionStackException
                or StackOverflowException
                or AccessViolationException
                or BadImageFormatException
                or InvalidProgramException
                or TypeInitializationException
                or System.Runtime.InteropServices.SEHException)
            {
                return false;
            }
        }

        if (exception is AggregateException aggregate)
        {
            return aggregate.InnerExceptions.All(IsRecoverable);
        }

        return true;
    }

    /// <summary>Formats a redacted crash report. <paramref name="error"/> is usually an Exception but may be any object.</summary>
    public static string Format(object? error, string source, DateTimeOffset timestamp, bool recovered,
        string? appVersion = null, string? userProfilePath = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Boo crash report");
        builder.Append("Time (UTC): ").AppendLine(timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        builder.Append("Source: ").AppendLine(string.IsNullOrWhiteSpace(source) ? "Unknown" : source);
        builder.Append("Recovered: ").AppendLine(recovered ? "yes" : "no");
        if (!string.IsNullOrWhiteSpace(appVersion))
        {
            builder.Append("Version: ").AppendLine(appVersion);
        }

        builder.Append("OS: ").AppendLine(Environment.OSVersion.VersionString);
        builder.Append("Runtime: ").AppendLine(Environment.Version.ToString());
        builder.AppendLine();

        var details = error switch
        {
            null => "(no exception object)",
            Exception ex => ex.ToString(),
            _ => error.GetType().FullName + ": " + error
        };
        builder.AppendLine(Redact(details, userProfilePath));
        return builder.ToString();
    }
}

/// <summary>
/// Stops Boo from limping along in a crash loop: a handful of recoveries in a short window is fine, a burst is not.
/// </summary>
public sealed class CrashLoopGuard
{
    private readonly int _maxRecoveries;
    private readonly TimeSpan _window;
    private readonly Queue<DateTimeOffset> _recent = new();
    private readonly object _gate = new();

    public CrashLoopGuard(int maxRecoveries = 5, TimeSpan? window = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRecoveries, 1);
        var span = window ?? TimeSpan.FromMinutes(1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(span, TimeSpan.Zero, nameof(window));
        _maxRecoveries = maxRecoveries;
        _window = span;
    }

    /// <summary>Records a failure at <paramref name="now"/>; true when Boo should keep running.</summary>
    public bool TryRecover(DateTimeOffset now)
    {
        lock (_gate)
        {
            while (_recent.Count > 0 && now - _recent.Peek() > _window)
            {
                _recent.Dequeue();
            }

            if (_recent.Count >= _maxRecoveries)
            {
                return false;
            }

            _recent.Enqueue(now);
            return true;
        }
    }
}
