namespace Boo.Core;

public enum RequestKind { Explain, Research, Task, Undo }

/// <summary>
/// Classifies typed or transcribed user text into the kind of request Boo
/// should handle.  The classification is deliberately literal so a user can
/// predict which phrases authorize actions and which stay read-only.
/// </summary>
public static class RequestIntent
{
    private static readonly string[] UndoPhrases =
    [
        "undo", "undo that", "undo it", "undo the edit", "undo the last edit", "undo last edit",
        "undo the change", "revert", "revert that", "revert it", "revert the edit", "put it back",
        "change it back"
    ];

    private static readonly string[] TaskPrefixes =
    [
        "task ", "please open ", "open ", "please create ", "create ", "type ", "click ", "edit ",
        "please edit ", "change ", "please change ", "set ", "please set ", "apply "
    ];

    private static readonly string[] ResearchPrefixes =
    [
        "find ", "search ", "research ", "look up ", "lookup ", "latest ", "what's the latest ",
        "what is the latest ", "compare "
    ];

    private static readonly string[] ResearchMarkers =
    [
        "with sources", "with links", "cite sources", "source links"
    ];

    public static RequestKind Classify(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return RequestKind.Explain;
        }

        if (IsUndo(trimmed))
        {
            return RequestKind.Undo;
        }

        if (TaskPrefixes.Any(prefix => trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return RequestKind.Task;
        }

        if (ResearchPrefixes.Any(prefix => trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
            ResearchMarkers.Any(marker => trimmed.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return RequestKind.Research;
        }

        return RequestKind.Explain;
    }

    private static bool IsUndo(string trimmed)
    {
        var candidate = trimmed.TrimEnd('.', '!').Trim();
        if (candidate.StartsWith("please ", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate["please ".Length..].Trim();
        }

        return UndoPhrases.Any(phrase => string.Equals(candidate, phrase, StringComparison.OrdinalIgnoreCase));
    }
}
