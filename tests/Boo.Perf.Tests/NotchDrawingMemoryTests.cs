using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using Microsoft.Win32;
using Xunit.Abstractions;
using Application = FlaUI.Core.Application;

namespace Boo.Perf.Tests;

/// <summary>
/// The notch is always on screen, so what it costs to draw is paid all day. On a PC with a graphics card, letting
/// Windows draw the small see-through notch on the card kept ~35 MB of driver memory from launch and added
/// ~35-50 MB more the first time the face came out (measured 2026-11-12; DECISIONS.md D-2026-11-12-notch-software-drawing).
/// The notch therefore draws in software. This test pins that as behaviour, two ways:
///   1. the running notch reports "SoftwareOnly" through BOO_PERF_DIAG (catches it on any machine, including a
///      CI VM with no graphics card, where both modes would cost the same memory);
///   2. one hover-and-retract adds at most <see cref="HoverGrowthBudgetMb"/> of private memory (catches the cost
///      itself on a real PC, whatever the cause).
/// Own launcher and own result folder (perf/notch-drawing) so the budget test's perf-results files stay its own.
/// </summary>
[Collection("Boo perf (serial)")]
public sealed class NotchDrawingMemoryTests : IDisposable
{
    /// <summary>Software drawing measured +17 MB on the dev PC; card drawing +36 (lane/face) to +48 MB (Living ink).</summary>
    public const double HoverGrowthBudgetMb = 30;

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Boo";

    private readonly ITestOutputHelper _output;
    private readonly string _outDir;
    private readonly string _diagPath;
    private readonly string _dataDir;
    private readonly string? _originalRunValue;
    private readonly UIA3Automation _automation = new();
    private Application? _app;

    public NotchDrawingMemoryTests(ITestOutputHelper output)
    {
        _output = output;
        var artifacts = Environment.GetEnvironmentVariable("BOO_UI_ARTIFACTS");
        var root = Path.GetFullPath(!string.IsNullOrWhiteSpace(artifacts) ? artifacts : Path.Combine(AppContext.BaseDirectory, "perf-artifacts"));
        _outDir = Path.Combine(root, "perf", "notch-drawing");
        Directory.CreateDirectory(_outDir);
        _diagPath = Path.Combine(_outDir, "diag.json");
        File.Delete(_diagPath);
        _dataDir = Path.Combine(Path.GetTempPath(), "boo-perf-draw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        _originalRunValue = run?.GetValue(RunValueName) as string;
    }

    [Fact]
    public void Notch_draws_in_software_and_one_hover_stays_within_memory_budget()
    {
        Assert.True(Wait(() => Process.GetProcessesByName("Boo").Length == 0, TimeSpan.FromSeconds(15)),
            "Boo is already running; it is single-instance, so this test cannot launch its own copy. Quit Boo and rerun.");

        var exe = ResolveExe();
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        start.Environment["BOO_QA_CAPTURABLE"] = "1";
        start.Environment["BOO_DATA_DIR"] = _dataDir;
        start.Environment["BOO_PERF_DIAG"] = _diagPath;
        ParkMouse();
        _app = Application.Launch(start);
        using var process = Process.GetProcessById(_app.ProcessId);

        Window? notch = null;
        Assert.True(Wait(() => (notch = FindNotch()) is not null, TimeSpan.FromSeconds(60)), "The notch window never appeared.");
        Assert.True(Wait(() => ReadDiag() is not null, TimeSpan.FromSeconds(30)), $"Boo never wrote BOO_PERF_DIAG ({_diagPath}).");

        // 1. Drawing mode, as the running notch reports it.
        string? mode = null;
        Wait(() => (mode = ReadDiag()?.RenderMode) is not null and not "", TimeSpan.FromSeconds(10));
        Log($"notchRenderMode = {mode ?? "(missing)"}");

        // 2. One hover and retract, measured the same way as the budget test (settle, hover 3.5 s, retract, settle).
        Thread.Sleep(TimeSpan.FromSeconds(6));
        process.Refresh();
        var before = process.PrivateMemorySize64;
        var bounds = notch!.BoundingRectangle;
        Mouse.MoveTo(new Point(bounds.Left + bounds.Width / 2, bounds.Top + 2));
        var revealed = Wait(() => ReadDiag()?.FaceState is { } s && s != "Hidden", TimeSpan.FromSeconds(3), pollMs: 100);
        Thread.Sleep(TimeSpan.FromMilliseconds(3500));
        ParkMouse();
        var retracted = Wait(() => ReadDiag()?.FaceState == "Hidden", TimeSpan.FromSeconds(10), pollMs: 100);
        Thread.Sleep(TimeSpan.FromSeconds(6));
        process.Refresh();
        var after = process.PrivateMemorySize64;
        var growthMb = Math.Round((after - before) / 1024.0 / 1024.0, 1);
        Log($"privateBeforeHoverMb = {Mb(before)}, privateAfterHoverMb = {Mb(after)}, hoverGrowthMb = {growthMb} (budget <= {HoverGrowthBudgetMb})");
        File.WriteAllText(Path.Combine(_outDir, "notch-drawing-results.md"),
            "| metric | value | budget |\n|---|---:|---:|\n" +
            $"| notchRenderMode | {mode} | == SoftwareOnly |\n" +
            $"| privateBeforeHoverMb | {Mb(before)} | - |\n| privateAfterHoverMb | {Mb(after)} | - |\n" +
            $"| hoverGrowthMb | {growthMb.ToString(CultureInfo.InvariantCulture)} | <= {HoverGrowthBudgetMb} |\n");

        Assert.True(revealed, "Hovering the notch never brought the face out, so the hover memory cost was not measured.");
        Assert.True(retracted, "The face never went back to Hidden after the pointer left.");
        Assert.True(mode == "SoftwareOnly",
            $"The notch draws in '{mode ?? "(not reported)"}' mode; it must be SoftwareOnly (graphics-card drawing keeps tens of MB of driver memory).");
        Assert.True(growthMb <= HoverGrowthBudgetMb,
            $"One hover added {growthMb} MB of private memory (budget {HoverGrowthBudgetMb} MB).");
    }

    public void Dispose()
    {
        try
        {
            if (_app is { HasExited: false })
            {
                _app.Kill();
            }
        }
        catch (Exception)
        {
            // Already gone.
        }
        _app?.Dispose();
        _automation.Dispose();
        using (var run = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true))
        {
            if (_originalRunValue is not null)
            {
                run.SetValue(RunValueName, _originalRunValue, RegistryValueKind.String);
            }
            else if (run.GetValue(RunValueName) is not null)
            {
                run.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (Exception)
        {
            // Temp folder; leaving it behind is harmless.
        }
    }

    private sealed record Diag(string? FaceState, string? RenderMode);

    private Diag? ReadDiag()
    {
        try
        {
            using var stream = new FileStream(_diagPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            return new Diag(
                root.TryGetProperty("faceState", out var f) ? f.GetString() : null,
                root.TryGetProperty("notchRenderMode", out var m) ? m.GetString() : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private Window? FindNotch()
    {
        try
        {
            return _app!.GetAllTopLevelWindows(_automation).FirstOrDefault(w =>
                PerfAppFixture.SafeName(w) is PerfAppFixture.NotchName or PerfAppFixture.NotchTitle);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void ParkMouse()
    {
        var desktop = _automation.GetDesktop().BoundingRectangle;
        Mouse.MoveTo(new Point(desktop.Right - 4, desktop.Bottom - 4));
    }

    private static string ResolveExe()
    {
        var configured = Environment.GetEnvironmentVariable("BOO_UI_EXE");
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Boo.sln")))
        {
            dir = dir.Parent;
        }
        var path = Path.GetFullPath(!string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Combine(dir?.FullName ?? ".", "artifacts", "ci", "Boo.exe"));
        Assert.True(File.Exists(path), $"Published Boo.exe not found at '{path}'. Publish it first or set BOO_UI_EXE.");
        return path;
    }

    private static bool Wait(Func<bool> condition, TimeSpan timeout, int pollMs = 200)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(pollMs);
        }
        return false;
    }

    private static string Mb(long bytes) => (bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture);

    private void Log(string message) => _output.WriteLine(message);
}
