using System.Globalization;

namespace Boo.Core;

/// <summary>
/// Round 3 (22 Nov 2026, Sprint 1 MVP pick 4): the one word drawn beside the eyes on the notch, like HeyClicky's
/// listening bar. "Listening" while the microphone is on, "Thinking…" while an answer runs, and the time left in a
/// running focus session (mm:ss) while the hanging panel is closed. Nothing else is ever written on the notch.
/// Pure rules, no clock: the focus seconds come from the panel's existing one-second focus tick.
/// </summary>
public static class NotchStatusWord
{
    public const string Listening = "Listening";
    public const string Thinking = "Thinking…";

    /// <summary>Automation id of the text element that carries the word (UI Automation reads its Name).</summary>
    public const string AutomationId = "NotchStatusWord";

    /// <summary>
    /// The word for <paramref name="state"/> given the app signals, or null when the notch shows no word.
    /// Listening and Thinking carry their word whatever else is going on. The focus time shows only on the resting
    /// faces (Hidden, Hover, Idle), only while a session is running, never while the panel is open and never when the
    /// user hid Boo. A caption state (Responding, Nudge), Capture, Paused and Happy show no word.
    /// </summary>
    public static string? For(FaceInputs inputs, NotchFaceState state)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return state switch
        {
            NotchFaceState.Listening => Listening,
            NotchFaceState.Thinking => Thinking,
            NotchFaceState.Hidden or NotchFaceState.Hover or NotchFaceState.Idle =>
                inputs.UserHidden || inputs.PanelOpen ? null : FocusTime(inputs.FocusRemainingSeconds),
            _ => null,
        };
    }

    /// <summary>"mm:ss" for the seconds left (minutes keep growing past 99: "240:00"), or null when no session runs.</summary>
    public static string? FocusTime(int? remainingSeconds)
    {
        if (remainingSeconds is not { } seconds)
        {
            return null;
        }
        seconds = Math.Max(0, seconds);
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60:00}:{seconds % 60:00}");
    }

    /// <summary>
    /// The text whose width decides the slot: every digit becomes an 8, so a ticking clock keeps one width and the
    /// blot does not breathe each second. Words come back unchanged.
    /// </summary>
    public static string WidthTemplate(string? word)
    {
        if (string.IsNullOrEmpty(word))
        {
            return string.Empty;
        }
        return string.Create(word.Length, word, static (span, text) =>
        {
            for (var i = 0; i < text.Length; i++)
            {
                span[i] = char.IsAsciiDigit(text[i]) ? '8' : text[i];
            }
        });
    }

    /// <summary>A word keeps the notch out without the pointer, exactly like a privacy disclosure does.</summary>
    public static bool Pins(string? word) => !string.IsNullOrEmpty(word);

    /// <summary>
    /// The body box the spec gives <paramref name="state"/>, widened when the eyes plus the word do not fit:
    /// side padding, both eyes and their gap, the slot gap, the word, side padding. The height never changes.
    /// </summary>
    public static SizeD Fit(NotchFaceSpec face, NotchFaceState state, double wordWidth)
    {
        ArgumentNullException.ThrowIfNull(face);
        var size = face.SizeFor(state);
        if (!double.IsFinite(wordWidth) || wordWidth <= 0)
        {
            return size;
        }
        var eyes = 2 * face.Eye.Width + face.Eye.Gap;
        var needed = 2 * LivingInk.SidePadding + eyes + LivingInk.SlotGap + wordWidth;
        return needed > size.Width ? new SizeD(needed, size.Height) : size;
    }
}
