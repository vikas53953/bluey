using Boo.Core;

namespace Boo.Storage;

/// <summary>
/// Writes redacted crash reports to &lt;data&gt;/logs/crash-&lt;utc&gt;.txt and keeps only the newest few. Every method
/// is best-effort and never throws: it runs while the process may already be failing.
/// </summary>
public sealed class CrashLogStore
{
    private readonly int _keep;

    public CrashLogStore(string dataDirectory, int keep = CrashReport.DefaultKeep)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("A data directory is required.", nameof(dataDirectory));
        ArgumentOutOfRangeException.ThrowIfLessThan(keep, 1);
        LogsDirectory = Path.Combine(Path.GetFullPath(dataDirectory), CrashReport.LogsFolderName);
        _keep = keep;
    }

    public string LogsDirectory { get; }

    /// <summary>Writes the report; returns the file path, or null when the write failed.</summary>
    public string? Write(object? error, string source, DateTimeOffset timestamp, bool recovered, string? appVersion = null)
    {
        try
        {
            Directory.CreateDirectory(LogsDirectory);
            var text = CrashReport.Format(error, source, timestamp, recovered, appVersion,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            var path = Path.Combine(LogsDirectory, CrashReport.FileNameFor(timestamp));
            for (var attempt = 1; File.Exists(path) && attempt < 100; attempt++)
            {
                path = Path.Combine(LogsDirectory, CrashReport.FileNameFor(timestamp.AddMilliseconds(attempt)));
            }

            File.WriteAllText(path, text);
            Prune();
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>True when a crash log newer than <paramref name="window"/> exists.</summary>
    public bool HasRecentCrash(DateTimeOffset now, TimeSpan window)
    {
        try
        {
            return Directory.Exists(LogsDirectory)
                && CrashReport.HasRecentCrash(FileNames(), now, window);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// True once per new crash log (within <paramref name="window"/>): records that the user has been told, so the
    /// "Boo recovered" notice is not repeated on every launch.
    /// </summary>
    public bool ConsumeRecoveredNotice(DateTimeOffset now, TimeSpan window)
    {
        try
        {
            if (!Directory.Exists(LogsDirectory))
            {
                return false;
            }

            var markerPath = Path.Combine(LogsDirectory, NoticeMarkerName);
            DateTimeOffset? lastNotified = null;
            if (File.Exists(markerPath)
                && DateTimeOffset.TryParse(File.ReadAllText(markerPath).Trim(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            {
                lastNotified = parsed;
            }

            var names = FileNames().ToList();
            if (!CrashReport.ShouldNotify(names, lastNotified, now, window))
            {
                return false;
            }

            File.WriteAllText(markerPath, CrashReport.NewestCrash(names)!.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private const string NoticeMarkerName = "last-crash-notice.txt";

    private void Prune()
    {
        foreach (var name in CrashReport.FilesToDelete(FileNames(), _keep))
        {
            try
            {
                File.Delete(Path.Combine(LogsDirectory, name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private IEnumerable<string> FileNames() =>
        Directory.EnumerateFiles(LogsDirectory, CrashReport.FilePrefix + "*" + CrashReport.FileExtension)
            .Select(Path.GetFileName)
            .OfType<string>();
}
