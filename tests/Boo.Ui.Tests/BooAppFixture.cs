using System.Diagnostics;
using System.Drawing;
using Application = FlaUI.Core.Application;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.Win32;

namespace Boo.Ui.Tests;

/// <summary>
/// Launches one published Boo.exe for the whole smoke run, isolated from the user's real data:
/// BOO_DATA_DIR points at a fresh temp folder and BOO_QA_CAPTURABLE=1 makes Boo's windows
/// visible to screenshots. The HKCU Run value "Boo" is snapshotted before and restored after.
/// Subclasses may pre-seed the data folder (for example with corrupt files) before Boo starts.
/// </summary>
public class BooAppFixture : IDisposable
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName = "Boo";
    public const string PanelTitlePrefix = "Boo companion";
    // The notch window's UIA Name comes from AutomationProperties.Name, not its Title ("Boo launcher").
    public const string NotchName = "Open Boo companion";
    public const string NotchTitle = "Boo launcher";

    private readonly string? _originalRunValue;
    private readonly bool _hadRunValue;

    public BooAppFixture()
        : this(seedDataDirectory: null)
    {
    }

    /// <param name="seedDataDirectory">Writes files into the fresh BOO_DATA_DIR before Boo is launched.</param>
    protected BooAppFixture(Action<string>? seedDataDirectory)
    {
        ExePath = ResolveExePath();
        ArtifactsDirectory = ResolveArtifactsDirectory();
        Directory.CreateDirectory(ArtifactsDirectory);

        // A previous fixture in this run may have just killed its Boo; give it a moment to exit fully.
        WaitUntil(() => Process.GetProcessesByName("Boo").Length == 0, TimeSpan.FromSeconds(15));
        var existing = Process.GetProcessesByName("Boo");
        if (existing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Boo is already running (pid {string.Join(", ", existing.Select(p => p.Id))}). " +
                "Boo is single-instance, so the smoke tests cannot launch their own copy. Quit Boo and rerun.");
        }

        using (var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false))
        {
            _originalRunValue = run?.GetValue(RunValueName) as string;
            _hadRunValue = _originalRunValue is not null;
        }

        DataDirectory = Path.Combine(Path.GetTempPath(), "boo-ui-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataDirectory);
        seedDataDirectory?.Invoke(DataDirectory);

        var start = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(ExePath)!,
        };
        start.Environment["BOO_QA_CAPTURABLE"] = "1";
        start.Environment["BOO_DATA_DIR"] = DataDirectory;

        Automation = new UIA3Automation();
        App = Application.Launch(start);
    }

    public string ExePath { get; }
    public string ArtifactsDirectory { get; }
    public string DataDirectory { get; }
    public Application App { get; }
    public UIA3Automation Automation { get; }

    public string AppearancePath => Path.Combine(DataDirectory, "appearance.json");
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");

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

    public Window WaitForPanel(TimeSpan timeout) =>
        WaitFor(FindPanel, timeout, "the Boo companion window to appear");

    public Window WaitForNotch(TimeSpan timeout) =>
        WaitFor(FindNotch, timeout, "the Boo launcher (notch) window to appear");

    public static void PressOpenShortcut() =>
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_B);

    /// <summary>Opens the panel with Ctrl+Alt+B if it is not already open, and returns it.</summary>
    public Window EnsurePanel()
    {
        var panel = FindPanel();
        if (panel is not null)
        {
            return panel;
        }

        PressOpenShortcut();
        return WaitForPanel(TimeSpan.FromSeconds(15));
    }

    public T WaitFor<T>(Func<T?> probe, TimeSpan timeout, string what) where T : class
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (probe() is { } value)
            {
                return value;
            }
            Thread.Sleep(200);
        }

        CaptureScreen("timeout-" + Sanitize(what));
        throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0}s waiting for {what}.");
    }

    public static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                // File may be mid-replace; poll again.
            }
            Thread.Sleep(200);
        }
        return false;
    }

    public string CaptureScreen(string name)
    {
        var path = Path.Combine(ArtifactsDirectory, Sanitize(name) + ".png");
        try
        {
            Capture.Screen().ToFile(path);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.ChangeExtension(path, ".error.txt"), ex.ToString());
        }
        return path;
    }

    public string CaptureRectangle(string name, Rectangle rectangle)
    {
        var path = Path.Combine(ArtifactsDirectory, Sanitize(name) + ".png");
        Capture.Rectangle(rectangle).ToFile(path);
        return path;
    }

    /// <summary>Reads a file Boo may be replacing (temp + File.Move) without blocking Boo's own move.</summary>
    public static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public string? ReadRunValue()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return run?.GetValue(RunValueName) as string;
    }

    public void WriteObservation(string name, string text) =>
        File.WriteAllText(Path.Combine(ArtifactsDirectory, Sanitize(name) + ".txt"), text);

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
            : Path.Combine(AppContext.BaseDirectory, "ui-artifacts"));
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
}
