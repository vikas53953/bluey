namespace Boo.Core;

/// <summary>One voice turn: time from end of the user's speech to the first audio of Boo's reply.</summary>
public sealed record LatencySample(int Turn, TimeSpan Latency, bool Cold);

/// <summary>
/// Records end-of-speech -> first-audio latency per voice turn (AE2). The first turn after a (re)connect is
/// "cold" and excluded from the statistics; all others are "warm".
/// Median: middle value, or the mean of the two middle values for an even count.
/// P95: nearest-rank method, i.e. the value at 1-based rank ceil(0.95 * n) of the ascending sorted warm samples
/// (for n = 20 that is the 19th value).
/// </summary>
public sealed class LatencyRecorder
{
    public const int TargetWarmTurns = 20;
    public const double MedianTargetMs = 1500;
    public const double P95TargetMs = 3000;

    /// <summary>A new recorder behaves as if just connected: its first recorded turn is cold.</summary>
    public LatencyRecorder() => throw new NotImplementedException();

    /// <summary>The realtime session (re)connected; the next recorded turn is cold.</summary>
    public void Connected() => throw new NotImplementedException();

    /// <summary>Records one turn. Throws <see cref="ArgumentException"/> if firstAudio is before endOfSpeech.</summary>
    public LatencySample Record(DateTimeOffset endOfSpeech, DateTimeOffset firstAudio) => throw new NotImplementedException();

    /// <summary>All samples in recording order, turns numbered from 1.</summary>
    public IReadOnlyList<LatencySample> Samples => throw new NotImplementedException();

    public int WarmCount => throw new NotImplementedException();

    public int ColdCount => throw new NotImplementedException();

    /// <summary>Median of warm turns in ms, or null when there are no warm samples.</summary>
    public double? WarmMedianMs() => throw new NotImplementedException();

    /// <summary>Nearest-rank p95 of warm turns in ms, or null when there are no warm samples.</summary>
    public double? WarmP95Ms() => throw new NotImplementedException();

    /// <summary>True when at least <see cref="TargetWarmTurns"/> warm turns exist, median &lt;= 1500 ms and p95 &lt;= 3000 ms.</summary>
    public bool MeetsTarget() => throw new NotImplementedException();
}
