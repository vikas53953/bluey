using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Application = FlaUI.Core.Application;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.UIA3;
using Microsoft.Win32;

namespace Boo.Perf.Tests;

/// <summary>
/// Launches one published Boo.exe for the perf run, isolated like the UI smoke fixture (fresh BOO_DATA_DIR,
/// BOO_QA_CAPTURABLE=1, HKCU Run value snapshotted and restored) and additionally points BOO_PERF_DIAG at
/// <c>&lt;artifacts&gt;/perf/diag.json</c> so Boo publishes its GC / render-loop counters every 500 ms.
/// Collects measured metrics and writes perf-results.json / perf-results.md on dispose.
/// </summary>
public sealed class PerfAppFixture : IDisposable
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName = "Boo";
    public const string PanelTitlePrefix = "Boo companion";
    // The notch window's UIA Name comes from AutomationProperties.Name, not its Title ("Boo launcher").
    public const string NotchName = "Open Boo companion";
    public const string NotchTitle = "Boo launcher";

    private static readonly JsonSerializerOptions DiagOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions ResultOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string? _originalRunValue;
    private readonly bool _hadRunValue;
    private readonly object _resultsLock = new();
    private readonly List<PerfResult> _results = [];
    private bool _disposed;

    public PerfAppFixture()
    {
        ExePath = ResolveExePath();
        ArtifactsDirectory = ResolveArtifactsDirectory();
        PerfDirectory = Path.Combine(ArtifactsDirectory, "perf");
        Directory.CreateDirectory(PerfDirectory);
        DiagPath = Path.Combine(PerfDirectory, "diag.json");
        if (File.Exists(DiagPath))
        {
            File.Delete(DiagPath);
        }

        Budgets = PerfBudgets.Load();

        // A previous fixture in this run may have just killed its Boo; give it a moment to exit fully.
        WaitUntil(() => Process.GetProcessesByName("Boo").Length == 0, TimeSpan.FromSeconds(15));
        var existing = Process.GetProcessesByName("Boo");
        if (existing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Boo is already running (pid {string.Join(", ", existing.Select(p => p.Id))}). " +
                "Boo is single-instance, so the perf tests cannot launch their own copy. Quit Boo and rerun.");
        }

        using (var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false))
        {
            _originalRunValue = run?.GetValue(RunValueName) as string;
            _hadRunValue = _originalRunValue is not null;
        }

        DataDirectory = Path.Combine(Path.GetTempPath(), "boo-perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataDirectory);

        var start = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(ExePath)!,
        };
        start.Environment["BOO_QA_CAPTURABLE"] = "1";
        start.Environment["BOO_DATA_DIR"] = DataDirectory;
        start.Environment["BOO_PERF_DIAG"] = DiagPath;

        Automation = new UIA3Automation();
        StartClock = Stopwatch.StartNew();
        App = Application.Launch(start);
        Process = Process.GetProcessById(App.ProcessId);
    }

    public string ExePath { get; }
    public string ArtifactsDirectory { get; }
    public string PerfDirectory { get; }
    public string DiagPath { get; }
    public string DataDirectory { get; }
    public PerfBudgets Budgets { get; }
    public Application App { get; }
    public Process Process { get; }
    public UIA3Automation Automation { get; }

    /// <summary>Started immediately before <see cref="Application.Launch(ProcessStartInfo)"/>.</summary>
    public Stopwatch StartClock { get; }

    public Window? FindTopLevel(Func<string, bool> titleMatches)
    {
        try
        {
            return App.GetAllTopLevelWindows(Automation)
                .FirstOrDefault(w => titleMatches(SafeName(w)));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public Window? FindPanel() => FindTopLevel(t => t.StartsWith(PanelTitlePrefix, StringComparison.Ordinal));

    public Window? FindNotch() => FindTopLevel(t =>
        string.Equals(t, NotchName, StringComparison.Ordinal) || string.Equals(t, NotchTitle, StringComparison.Ordinal));

    public bool DiagExists() => File.Exists(DiagPath);

    /// <summary>
    /// Reads the current diag.json. Boo replaces it atomically (temp + move), so the read shares
    /// ReadWrite|Delete and retries briefly when the file is mid-replace or half-written.
    /// </summary>
    public DiagSnapshot ReadDiag(TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        var clock = Stopwatch.StartNew();
        Exception? last = null;
        while (clock.Elapsed < limit)
        {
            try
            {
                using var stream = new FileStream(DiagPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var snapshot = JsonSerializer.Deserialize<DiagSnapshot>(stream, DiagOptions);
                if (snapshot is not null)
                {
                    return snapshot;
                }
                last = new InvalidDataException("diag.json deserialized to null.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                last = ex;
            }
            Thread.Sleep(50);
        }

        throw new TimeoutException(
            $"Could not read a complete diag snapshot from '{DiagPath}' within {limit.TotalSeconds:0}s " +
            "(is Boo honouring BOO_PERF_DIAG?).", last);
    }

    public static bool WaitUntil(Func<bool> condition, TimeSpan timeout, int pollMs = 200)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            try
            {
                if (condition())
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // File may be mid-replace; poll again.
            }
            Thread.Sleep(pollMs);
        }
        return false;
    }

    public string CaptureScreen(string name)
    {
        var path = Path.Combine(PerfDirectory, Sanitize(name) + ".png");
        try
        {
            if (FullDesktopCaptureAllowed())
            {
                using var image = Capture.Screen();
                image.ToFile(path);
            }
            else
            {
                CaptureBooWindows(path);
            }
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.ChangeExtension(path, ".error.txt"), ex.ToString());
        }
        return path;
    }

    /// <summary>
    /// Whole-desktop screenshots are allowed only on a GitHub-hosted runner (a throwaway VM) and never when the run asks
    /// for Boo's windows only (<c>BOO_CAPTURE_SCOPE=boo-windows</c>, which the live AI smoke always sets). Anywhere else
    /// (the owner's or a developer's own PC, a self-hosted runner) only Boo's own windows are captured, so a test run can
    /// never photograph someone's real screen.
    /// </summary>
    public static bool FullDesktopCaptureAllowed() =>
        string.Equals(Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"), "github-hosted", StringComparison.Ordinal)
        && !string.Equals(Environment.GetEnvironmentVariable("BOO_CAPTURE_SCOPE"), "boo-windows", StringComparison.OrdinalIgnoreCase);

    /// <summary>Saves each visible top-level window of the Boo process (name.png, name-1.png, ...); nothing else.</summary>
    private void CaptureBooWindows(string path)
    {
        var saved = 0;
        var windows = App.HasExited ? Array.Empty<Window>() : App.GetAllTopLevelWindows(Automation);
        foreach (var window in windows)
        {
            var target = saved == 0
                ? path
                : Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-" + saved + ".png");
            try
            {
                var bounds = window.BoundingRectangle;
                if (bounds.Width <= 0 || bounds.Height <= 0 || window.IsOffscreen)
                {
                    continue;
                }
                using var image = Capture.Element(window);
                image.ToFile(target);
                saved++;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.ChangeExtension(target, ".error.txt"), ex.ToString());
            }
        }
        if (saved == 0)
        {
            File.WriteAllText(Path.ChangeExtension(path, ".no-window.txt"),
                "No visible Boo window to capture. The full desktop is captured only on a GitHub-hosted runner.");
        }
    }

    public string CaptureRectangle(string name, Rectangle rectangle)
    {
        var path = Path.Combine(PerfDirectory, Sanitize(name) + ".png");
        try
        {
            using var image = Capture.Rectangle(rectangle);
            image.ToFile(path);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.ChangeExtension(path, ".error.txt"), ex.ToString());
        }
        return path;
    }

    /// <summary>Records an informational metric (no budget).</summary>
    public void WriteResult(string name, object? value) => WriteResult(name, value, budget: null, pass: null);

    /// <summary>Records a metric with the budget it was judged against and whether it passed.</summary>
    public void WriteResult(string name, object? value, string? budget, bool? pass)
    {
        lock (_resultsLock)
        {
            _results.RemoveAll(r => string.Equals(r.Name, name, StringComparison.Ordinal));
            _results.Add(new PerfResult(name, value, budget, pass));
        }
    }

    public static string SafeName(AutomationElement element)
    {
        try
        {
            return element.Name ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private void FlushResults()
    {
        List<PerfResult> snapshot;
        lock (_resultsLock)
        {
            snapshot = [.. _results];
        }

        var json = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var r in snapshot)
        {
            json[r.Name] = r.Budget is null && r.Pass is null
                ? r.Value
                : new { value = r.Value, budget = r.Budget, pass = r.Pass };
        }
        var document = new
        {
            schema = 1,
            writtenAtUtc = DateTimeOffset.UtcNow,
            exe = ExePath,
            budgetsFile = PerfBudgets.ResolvePath(),
            budgets = Budgets,
            metrics = json,
        };
        File.WriteAllText(Path.Combine(PerfDirectory, "perf-results.json"), JsonSerializer.Serialize(document, ResultOptions));

        var md = new StringBuilder();
        md.AppendLine("# Boo perf budgets");
        md.AppendLine();
        md.Append("Exe: `").Append(ExePath).AppendLine("`  ");
        md.Append("Budgets: `").Append(PerfBudgets.ResolvePath()).AppendLine("`  ");
        md.Append("Written: ").AppendLine(DateTimeOffset.UtcNow.ToString("u", CultureInfo.InvariantCulture));
        md.AppendLine();
        md.AppendLine("| metric | value | budget | pass/fail |");
        md.AppendLine("|---|---:|---:|:---:|");
        foreach (var r in snapshot)
        {
            var status = r.Pass switch
            {
                true => "PASS",
                false => "FAIL",
                null => "info",
            };
            md.Append("| ").Append(r.Name)
              .Append(" | ").Append(FormatValue(r.Value))
              .Append(" | ").Append(r.Budget ?? "-")
              .Append(" | ").Append(status)
              .AppendLine(" |");
        }
        File.WriteAllText(Path.Combine(PerfDirectory, "perf-results.md"), md.ToString());
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "-",
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        float f => f.ToString("0.###", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "-",
    };

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));

    private static string ResolveExePath()
    {
        var configured = Environment.GetEnvironmentVariable("BOO_UI_EXE");
        var candidate = !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Combine(FindRepoRoot(), "artifacts", "ci", "Boo.exe");
        var full = Path.GetFullPath(candidate);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException(
                $"Published Boo.exe not found at '{full}'. Publish it first " +
                "(dotnet publish src/Boo.App/Boo.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/ci) " +
                "or set BOO_UI_EXE.", full);
        }
        return full;
    }

    private static string ResolveArtifactsDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("BOO_UI_ARTIFACTS");
        return Path.GetFullPath(!string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, "perf-artifacts"));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Boo.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            FlushResults();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(PerfDirectory, "perf-results.error.txt"), ex.ToString());
        }

        try
        {
            CaptureScreen("zz-final-desktop");
        }
        catch (Exception)
        {
            // Best effort evidence only.
        }

        try
        {
            if (!App.HasExited)
            {
                App.Kill();
            }
        }
        catch (Exception)
        {
            // Already gone.
        }
        Process.Dispose();
        App.Dispose();
        Automation.Dispose();

        // Never leave the CI user's (or a developer's) Run key changed by this run.
        using (var run = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true))
        {
            if (_hadRunValue)
            {
                run.SetValue(RunValueName, _originalRunValue!, RegistryValueKind.String);
            }
            else if (run.GetValue(RunValueName) is not null)
            {
                run.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }

        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (Exception)
        {
            // Temp folder; leaving it behind is harmless.
        }
    }

    private sealed record PerfResult(string Name, object? Value, string? Budget, bool? Pass);
}
