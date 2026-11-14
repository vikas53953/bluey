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
    public static string BuildCommand(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            throw new ArgumentException("Executable path is required.", nameof(exePath));
        if (exePath.Contains('"', StringComparison.Ordinal))
            throw new ArgumentException("Executable path must not contain a double quote.", nameof(exePath));
        return "\"" + exePath + "\" " + BackgroundArgument;
    }

    /// <summary>
    /// Returns a plain reason when <paramref name="exePath"/> is a poor start-with-Windows target,
    /// otherwise null. A copy run from the temp folder (for example Boo.exe opened straight from
    /// inside the ZIP) disappears or can be replaced by anything else that writes to %TEMP%, so the
    /// Run key must not point there.
    /// </summary>
    public static string? UnstableLocationReason(string exePath, string tempDirectory)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return "Boo could not find its own program file.";
        if (!Path.IsPathFullyQualified(exePath))
            return "Boo's program path is not a full path.";
        if (string.IsNullOrWhiteSpace(tempDirectory))
            return null;

        var exe = Path.GetFullPath(exePath);
        var temp = Path.GetFullPath(tempDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return exe.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
            ? "Boo is running from a temporary folder (probably straight from the ZIP). Extract the ZIP to a folder first, then turn on Start with Windows."
            : null;
    }

    /// <summary>Computes the change needed given the current Run value (null when absent).</summary>
    public static StartupChange Plan(bool startWithWindows, string exePath, string? currentValue)
    {
        if (!startWithWindows)
            return currentValue is null ? new StartupChange(StartupChangeKind.None, null) : new StartupChange(StartupChangeKind.Remove, null);
        var command = BuildCommand(exePath);
        return string.Equals(command, currentValue, StringComparison.OrdinalIgnoreCase)
            ? new StartupChange(StartupChangeKind.None, null)
            : new StartupChange(StartupChangeKind.Write, command);
    }

    /// <summary>Reads the current value, plans, performs at most one Write or Delete, and returns the plan.</summary>
    public static StartupChange Apply(bool startWithWindows, string exePath, IStartupRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var change = Plan(startWithWindows, exePath, registry.Read(ValueName));
        switch (change.Kind)
        {
            case StartupChangeKind.Write:
                registry.Write(ValueName, change.Command!);
                break;
            case StartupChangeKind.Remove:
                registry.Delete(ValueName);
                break;
        }

        return change;
    }
}
