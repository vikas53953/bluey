namespace Boo.Core;

/// <summary>
/// Thin seam over HKCU\Software\Microsoft\Windows\CurrentVersion\Run. The Windows
/// implementation (Microsoft.Win32.Registry) lives outside Boo.Core; tests use a fake.
/// </summary>
public interface IStartupRegistry
{
    /// <summary>Returns the string value with this name under the Run key, or null when absent.</summary>
    string? Read(string valueName);

    void Write(string valueName, string command);

    void Delete(string valueName);
}

public enum StartupChangeKind
{
    None,
    Write,
    Remove
}

/// <summary>What must change in the Run key. <see cref="Command"/> is set only for Write.</summary>
public sealed record StartupChange(StartupChangeKind Kind, string? Command);

/// <summary>
/// Pure start-with-Windows logic (setting AppSettings.StartWithWindows, default off).
/// Enabled: the Run value "Boo" must equal <c>"&lt;exe path&gt;" --background</c> (path always
/// double-quoted). Disabled: the value must be absent. Planning is idempotent: when the
/// registry already matches (command compared case-insensitively, as Windows paths are), the
/// plan is None and Apply touches nothing. Only the "Boo" value is ever written or deleted.
/// </summary>
public static class StartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Boo";
    public const string BackgroundArgument = "--background";

    /// <summary>
    /// Builds <c>"&lt;exePath&gt;" --background</c>. Throws <see cref="ArgumentException"/> when
    /// exePath is null, empty, whitespace, or contains a double-quote character.
    /// </summary>
    public static string BuildCommand(string exePath) => throw new NotImplementedException();

    /// <summary>Computes the change needed given the current Run value (null when absent).</summary>
    public static StartupChange Plan(bool startWithWindows, string exePath, string? currentValue) =>
        throw new NotImplementedException();

    /// <summary>Reads the current value, plans, performs at most one Write or Delete, and returns the plan.</summary>
    public static StartupChange Apply(bool startWithWindows, string exePath, IStartupRegistry registry) =>
        throw new NotImplementedException();
}
