using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Boo.Core;

/// <summary>The focus and sleep tools Boo's Realtime session exposes (Riley's voice focus flow).</summary>
public enum VoiceCommandKind { FocusStart, FocusPause, FocusResume, FocusEnd, FocusStatus, GoToSleep }

/// <summary>A cleaned voice command. Task and Minutes are only meaningful for FocusStart.</summary>
public sealed record VoiceCommand(VoiceCommandKind Kind, string? Task = null, double Minutes = 0);

/// <summary>
/// Turns a model tool call (name + JSON arguments) into a <see cref="VoiceCommand"/>. The arguments come from the
/// model, so they are treated as untrusted: the task is cleaned and capped, minutes are clamped, and every failure
/// yields a short sentence that is safe to read back to the user.
/// </summary>
public static class VoiceCommandParser
{
    public const double DefaultMinutes = 30, MinMinutes = 1, MaxMinutes = 240;
    public const int MaxTaskLength = 120;

    private const string NoTaskError = "Say what you want to focus on.";
    private const string MalformedError = "Boo could not read that request.";
    private const string UnknownError = "Unknown voice command.";

    private static readonly string[] Names =
    {
        "focus_start", "focus_pause", "focus_resume", "focus_end", "focus_status", "go_to_sleep",
    };

    private static readonly VoiceCommandKind[] Kinds =
    {
        VoiceCommandKind.FocusStart, VoiceCommandKind.FocusPause, VoiceCommandKind.FocusResume,
        VoiceCommandKind.FocusEnd, VoiceCommandKind.FocusStatus, VoiceCommandKind.GoToSleep,
    };

    public static IReadOnlyList<string> ToolNames { get; } = Array.AsReadOnly(Names);

    public static bool IsVoiceTool(string? name) => name is not null && Array.IndexOf(Names, name) >= 0;

    public static bool TryParse(string? toolName, string? argumentsJson, out VoiceCommand? command, out string? error)
    {
        command = null;
        error = null;

        var index = toolName is null ? -1 : Array.IndexOf(Names, toolName);
        if (index < 0)
        {
            error = UnknownError;
            return false;
        }

        var kind = Kinds[index];
        if (kind != VoiceCommandKind.FocusStart)
        {
            // The no-argument tools ignore whatever the model sent.
            command = new VoiceCommand(kind);
            return true;
        }

        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            error = NoTaskError;
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = MalformedError;
                return false;
            }

            var task = root.TryGetProperty("task", out var taskElement) && taskElement.ValueKind == JsonValueKind.String
                ? CleanTask(taskElement.GetString())
                : string.Empty;
            if (task.Length == 0)
            {
                error = NoTaskError;
                return false;
            }

            var minutes = root.TryGetProperty("minutes", out var minutesElement)
                ? ReadMinutes(minutesElement)
                : DefaultMinutes;

            command = new VoiceCommand(VoiceCommandKind.FocusStart, task, minutes);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            // ArgumentException: the text holds an unpaired UTF-16 surrogate, which JsonDocument cannot transcode.
            error = MalformedError;
            return false;
        }
    }

    private static string CleanTask(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(raw.Length);
        var pendingSpace = false;
        foreach (var c in raw)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (char.IsControl(c))
            {
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        var cleaned = builder.ToString();
        if (cleaned.Length > MaxTaskLength)
        {
            cleaned = cleaned[..MaxTaskLength];
            if (char.IsHighSurrogate(cleaned[^1]))
            {
                cleaned = cleaned[..^1];
            }

            cleaned = cleaned.TrimEnd();
        }

        return cleaned;
    }

    private static double ReadMinutes(JsonElement element)
    {
        double value;
        switch (element.ValueKind)
        {
            case JsonValueKind.Number when element.TryGetDouble(out value):
                break;
            case JsonValueKind.String when double.TryParse(
                element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value):
                break;
            default:
                return DefaultMinutes;
        }

        if (!double.IsFinite(value))
        {
            return DefaultMinutes;
        }

        return Math.Clamp(value, MinMinutes, MaxMinutes);
    }
}

/// <summary>What to do with a voice command and the exact sentence Boo says back.</summary>
public sealed record VoiceFocusDecision(bool Apply, string Reply);

/// <summary>Decides whether a voice command applies to the current focus session, and phrases the reply.</summary>
public static class VoiceFocusPolicy
{
    /// <summary>Reply when a hands-free turn (server VAD) tries to pause or end a session the user is in.</summary>
    public const string HandsFreeRefusal = "Hold Boo's eyes or the talk shortcut while you say that to change a running focus session.";

    /// <param name="handsFree">
    /// True when the microphone is open continuously (Talk mode, server VAD) rather than only while the user holds
    /// to talk. In that mode the model also hears speakers, videos and other people, so a tool call cannot be
    /// attributed to the user. Commands that disturb a session the user is in (pause, end) are refused; starting,
    /// status and going quiet are harmless and stay available.
    /// </param>
    public static VoiceFocusDecision Decide(VoiceCommand command, FocusSnapshot? current, double remainingSeconds = 0, bool handsFree = false)
    {
        ArgumentNullException.ThrowIfNull(command);

        var status = current?.Status;
        var active = status is FocusStatus.Running or FocusStatus.Paused;

        if (handsFree && active && command.Kind is VoiceCommandKind.FocusPause or VoiceCommandKind.FocusEnd)
            return new VoiceFocusDecision(false, HandsFreeRefusal);

        return command.Kind switch
        {
            VoiceCommandKind.FocusStart => active
                ? new VoiceFocusDecision(false, $"A focus session is already going ({current!.Task}). End it first.")
                : new VoiceFocusDecision(true, $"Focus started: {command.Task} for {Whole(Math.Round(command.Minutes))} minutes."),
            VoiceCommandKind.FocusPause => status == FocusStatus.Running
                ? new VoiceFocusDecision(true, "Focus paused.")
                : new VoiceFocusDecision(false, "There is no running focus session to pause."),
            VoiceCommandKind.FocusResume => status == FocusStatus.Paused
                ? new VoiceFocusDecision(true, "Focus resumed.")
                : new VoiceFocusDecision(false, "There is no paused focus session to resume."),
            VoiceCommandKind.FocusEnd => active
                ? new VoiceFocusDecision(true, "Focus ended.")
                : new VoiceFocusDecision(false, "There is no focus session to end."),
            VoiceCommandKind.FocusStatus => status switch
            {
                FocusStatus.Running => new VoiceFocusDecision(false, $"Focusing on {current!.Task}, {MinutesLeft(remainingSeconds)} minutes left."),
                FocusStatus.Paused => new VoiceFocusDecision(false, $"Focus on {current!.Task} is paused with {MinutesLeft(remainingSeconds)} minutes left."),
                _ => new VoiceFocusDecision(false, "No focus session is running."),
            },
            VoiceCommandKind.GoToSleep => new VoiceFocusDecision(true, "Going quiet."),
            _ => new VoiceFocusDecision(false, "Unknown voice command."),
        };
    }

    private static string MinutesLeft(double remainingSeconds)
    {
        var left = double.IsFinite(remainingSeconds) ? Math.Ceiling(remainingSeconds / 60) : 1;
        return Whole(Math.Max(1, left));
    }

    private static string Whole(double minutes) => ((int)minutes).ToString(CultureInfo.InvariantCulture);
}
