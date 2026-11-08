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

    private readonly List<LatencySample> _samples = new();
    private bool _nextCold = true;

    /// <summary>A new recorder behaves as if just connected: its first recorded turn is cold.</summary>
    public LatencyRecorder()
    {
    }

    /// <summary>The realtime session (re)connected; the next recorded turn is cold.</summary>
    public void Connected() => _nextCold = true;

    /// <summary>Records one turn. Throws <see cref="ArgumentException"/> if firstAudio is before endOfSpeech.</summary>
    public LatencySample Record(DateTimeOffset endOfSpeech, DateTimeOffset firstAudio)
    {
        var latency = firstAudio - endOfSpeech;
        if (latency < TimeSpan.Zero)
        {
            throw new ArgumentException("First audio cannot precede end of speech.", nameof(firstAudio));
        }

        var sample = new LatencySample(_samples.Count + 1, latency, _nextCold);
        _nextCold = false;
        _samples.Add(sample);
        return sample;
    }

    /// <summary>All samples in recording order, turns numbered from 1.</summary>
    public IReadOnlyList<LatencySample> Samples => _samples;

    public int WarmCount => _samples.Count(s => !s.Cold);

    public int ColdCount => _samples.Count(s => s.Cold);

    /// <summary>Median of warm turns in ms, or null when there are no warm samples.</summary>
    public double? WarmMedianMs()
    {
        var warm = SortedWarm();
        if (warm.Length == 0)
        {
            return null;
        }

        var mid = warm.Length / 2;
        return warm.Length % 2 == 1 ? warm[mid] : (warm[mid - 1] + warm[mid]) / 2.0;
    }

    /// <summary>Nearest-rank p95 of warm turns in ms, or null when there are no warm samples.</summary>
    public double? WarmP95Ms()
    {
        var warm = SortedWarm();
        if (warm.Length == 0)
        {
            return null;
        }

        var rank = (int)Math.Ceiling(0.95 * warm.Length);
        return warm[Math.Clamp(rank, 1, warm.Length) - 1];
    }

    /// <summary>True when at least <see cref="TargetWarmTurns"/> warm turns exist, median &lt;= 1500 ms and p95 &lt;= 3000 ms.</summary>
    public bool MeetsTarget() =>
        WarmCount >= TargetWarmTurns
        && WarmMedianMs() <= MedianTargetMs
        && WarmP95Ms() <= P95TargetMs;

    private double[] SortedWarm() =>
        _samples.Where(s => !s.Cold).Select(s => s.Latency.TotalMilliseconds).Order().ToArray();
}
