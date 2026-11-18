namespace Boo.Core;

/// <summary>
/// Decides whether a streamed caption continues the text the notch is already typing (the typewriter keeps its
/// place) or is a new caption (it restarts). The comparison uses the full, whitespace-normalised text Boo was given,
/// never the displayed window: a long reply is drawn as "…" plus its last ~160 characters, and two consecutive
/// windows of a growing reply share no common prefix even though the reply only grew. Comparing windows made the
/// caption blank and retype from scratch on every transcript delta past 160 characters.
/// </summary>
public static class CaptionStream
{
    /// <summary>Collapses all whitespace runs to single spaces (the same normalisation the notch draws).</summary>
    public static string Normalize(string? caption)
        => string.IsNullOrWhiteSpace(caption)
            ? string.Empty
            : string.Join(' ', caption.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>True when <paramref name="next"/> is <paramref name="previous"/> with more text appended (or the same text).</summary>
    public static bool Continues(string? previous, string? next)
    {
        var before = Normalize(previous);
        var after = Normalize(next);
        return before.Length > 0 && after.Length >= before.Length && after.StartsWith(before, StringComparison.Ordinal);
    }
}
