namespace Boo.Core;

/// <summary>
/// The status line's "Boo is working" message. It is shown only for the request that is still current, and Stop replaces
/// it with a stopped message only while it is still the text on screen.
/// </summary>
public sealed class BusyStatusLine
{
    private readonly Func<long, bool> _isCurrent;
    private string? _busyText;

    /// <param name="isCurrent">True while the request of the given generation is still the current one.</param>
    public BusyStatusLine(Func<long, bool> isCurrent)
    {
        ArgumentNullException.ThrowIfNull(isCurrent);
        _isCurrent = isCurrent;
    }

    /// <summary>
    /// Records <paramref name="text"/> as the working message of the request of <paramref name="generation"/> and returns
    /// it for display, or returns null (show nothing, keep the current owner) when that request is no longer current.
    /// </summary>
    public string? Show(string text, long generation)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!_isCurrent(generation))
        {
            return null;
        }
        _busyText = text;
        return text;
    }

    /// <summary>
    /// Called on Stop. Returns <paramref name="stoppedText"/> when <paramref name="shownText"/> is still the working
    /// message, otherwise null (leave the status line as it is). Either way no working message is owned afterwards.
    /// </summary>
    public string? AfterStop(string? shownText, string stoppedText)
    {
        ArgumentNullException.ThrowIfNull(stoppedText);
        var replace = _busyText is not null && string.Equals(shownText, _busyText, StringComparison.Ordinal);
        _busyText = null;
        return replace ? stoppedText : null;
    }
}
