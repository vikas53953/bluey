namespace Boo.Core;

/// <summary>
/// Local checks for actions that send input to another window (click, type_text,
/// inspect). The model only names a target as free text, so the approval card must
/// show the real window that will receive the input, and some windows are never
/// valid targets however the request is worded: a shell or terminal turns typed
/// text into commands, and Boo must never drive its own approval UI.
/// </summary>
public static class WindowActionGuard
{
    public const int MaximumTypedCharacters = 1024;

    private static readonly HashSet<string> WindowTools = new(StringComparer.Ordinal)
    {
        "click", "type_text", "inspect"
    };

    private static readonly HashSet<string> InputTools = new(StringComparer.Ordinal)
    {
        "click", "type_text"
    };

    // Processes where typed text or a click can run commands or change system
    // configuration. Compared without ".exe", case-insensitively.
    private static readonly HashSet<string> CommandHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "powershell_ise", "windowsterminal", "wt", "openconsole", "conhost",
        "bash", "wsl", "wslhost", "mintty", "ubuntu", "debian", "kali", "putty", "kitty",
        "regedit", "mmc", "taskmgr", "msiexec", "consent", "credentialuibroker"
    };

    public static bool IsWindowTool(string? toolName) => toolName is not null && WindowTools.Contains(toolName);

    /// <summary>Plain description of the window that will receive the action.</summary>
    public static string Describe(WindowIdentity window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var name = string.IsNullOrWhiteSpace(window.ProcessName) ? "unknown app" : window.ProcessName.Trim();
        return $"{name} window (process {window.ProcessId})";
    }

    /// <summary>
    /// Returns null when the action may be offered for approval against this window,
    /// otherwise a plain reason it is blocked. Non-window tools always pass.
    /// </summary>
    public static string? BlockedReason(ToolRequest request, WindowIdentity? window, int booProcessId)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsWindowTool(request.Name))
        {
            return null;
        }

        if (window is null)
        {
            return "No target window is selected, so Boo cannot show where this action would go.";
        }

        if (booProcessId > 0 && window.ProcessId == booProcessId)
        {
            return "Boo will not act on its own windows.";
        }

        if (InputTools.Contains(request.Name) && IsCommandHost(window.ProcessName))
        {
            return $"Boo will not click or type into {NormalizeProcessName(window.ProcessName)}: input there can run commands or change system settings.";
        }

        if (request.Name == "type_text" &&
            request.Arguments is not null &&
            request.Arguments.TryGetValue("text", out var text) &&
            text is not null)
        {
            if (text.Length > MaximumTypedCharacters)
            {
                return $"Text exceeds Boo's bounded {MaximumTypedCharacters}-character input limit.";
            }

            if (HasDisallowedControlCharacter(text))
            {
                return "The text contains hidden control characters; Boo only types visible text, tabs and line breaks.";
            }
        }

        return null;
    }

    public static bool IsCommandHost(string? processName) =>
        !string.IsNullOrWhiteSpace(processName) && CommandHosts.Contains(NormalizeProcessName(processName));

    internal static bool HasDisallowedControlCharacter(string text)
    {
        foreach (var character in text)
        {
            if (character is '\t' or '\n' or '\r')
            {
                continue;
            }

            if (char.IsControl(character))
            {
                return true;
            }

            // Bidirectional overrides and zero-width characters can make the
            // approval card show different text from what is typed.
            if (character is '​' or '‌' or '‍' or '⁠' or '﻿' ||
                (character >= '‪' && character <= '‮') ||
                (character >= '⁦' && character <= '⁩'))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeProcessName(string processName)
    {
        var trimmed = processName.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }
}
