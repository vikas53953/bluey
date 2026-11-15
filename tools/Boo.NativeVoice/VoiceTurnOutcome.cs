namespace Boo.NativeVoice;

/// <summary>
/// Classifies one bounded QA voice tool turn from the panel's ordered status history plus the virtual device's
/// cumulative rendered-byte counter. Pure, so the offline unit suite can pin the orderings a live run produced.
/// The turn has two responses: the forced screen_help call, then the follow-up asked for after Boo sent the
/// function result. Only the follow-up's own terminal status counts, so a terminal text is accepted only when it
/// is the last status AND comes after the tool boundary (the status RenderReply writes before the function result
/// is sent), AND audio was consumed after that boundary. Anything else, including a terminal status seen before
/// the boundary (that was the first response ending), is NotEnded.
/// </summary>
public static class VoiceTurnOutcome
{
    public const string NothingOfferedStatus = "Boo proposed a screen action, but no screen observation backs it. Nothing was offered.";
    public const string NoActionStatus = "Reply complete. No action was committed.";
    public const string SpokenCompleteStatus = "Reply complete. Text and spoken output share the same turn.";
    /// <summary>The hosted QA session is bounded to 128 output tokens; a follow-up cut by that cap is reported as cap-truncated, never as completed.</summary>
    public const string CapTruncatedStatus = "Realtime response incomplete (max_output_tokens).";

    public enum Kind { NotEnded, Completed, CapTruncated }

    /// <summary>Index of the first status RenderReply wrote for the tool's structured reply, or -1 if the guard was not reached.</summary>
    public static int ToolBoundary(IReadOnlyList<string> statuses)
    {
        for (var i = 0; i < statuses.Count; i++)
        {
            if (statuses[i] == NothingOfferedStatus || statuses[i] == NoActionStatus
                || statuses[i].StartsWith("Review", StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <param name="renderedAtBoundary">Cumulative rendered bytes when the boundary status was first observed.</param>
    /// <param name="renderedNow">Cumulative rendered bytes now; audio after the boundary belongs to the follow-up.</param>
    public static Kind Classify(IReadOnlyList<string> statuses, long renderedAtBoundary, long renderedNow)
    {
        var boundary = ToolBoundary(statuses);
        if (boundary < 0 || boundary == statuses.Count - 1) return Kind.NotEnded;
        if (renderedNow <= renderedAtBoundary) return Kind.NotEnded;
        return statuses[^1] switch
        {
            SpokenCompleteStatus => Kind.Completed,
            CapTruncatedStatus => Kind.CapTruncated,
            _ => Kind.NotEnded
        };
    }

    public static string Describe(Kind kind) => kind switch
    {
        Kind.Completed => "completed",
        Kind.CapTruncated => "cap-truncated (128-token QA bound)",
        _ => "not ended"
    };
}
