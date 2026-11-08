namespace Boo.Core;

public sealed class FocusSession
{
    private const double MinimumDuration = 60;
    private const double MaximumDuration = 14_400;
    private readonly List<FocusCheck> _checks = new();
    private readonly Guid _id = Guid.NewGuid();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private string _task;
    private double _duration;
    private double _elapsed;
    private double _realElapsed;
    private FocusStatus _status;
    private bool _developerMode;
    private bool _usedDeveloperMode;

    public FocusSession(string task, double duration = 1800, bool developerMode = false)
    {
        ArgumentNullException.ThrowIfNull(task);
        _task = task[..Math.Min(500, task.Length)];
        _duration = double.IsNaN(duration)
            ? MinimumDuration
            : Math.Clamp(duration, MinimumDuration, MaximumDuration);
        _status = FocusStatus.Running;
        _developerMode = developerMode;
        _usedDeveloperMode = developerMode;
    }

    public FocusSnapshot Snapshot => new(
        _id,
        _task,
        _duration,
        _startedAt,
        _elapsed,
        _realElapsed,
        _status,
        _developerMode,
        _usedDeveloperMode,
        _checks.ToArray());

    public double Remaining => Math.Max(0, _duration - _elapsed);

    /// <summary>Real seconds between checks; developer mode checks every ten seconds.</summary>
    public double CheckInterval => _developerMode ? 10 : 30;

    public bool ShouldMonitor => _status == FocusStatus.Running;

    public void Advance(double seconds)
    {
        if (!ShouldMonitor || !double.IsFinite(seconds) || seconds <= 0)
            return;

        var clockRate = _developerMode ? 4 : 1;
        _realElapsed += Math.Min(seconds, Remaining / clockRate);
        _elapsed = Math.Min(_duration, _elapsed + seconds * clockRate);

        if (_elapsed >= _duration)
            _status = FocusStatus.Ended;
    }

    public void Pause(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (_status != FocusStatus.Running)
            return;

        _status = FocusStatus.Paused;
        _checks.Add(new FocusCheck(_elapsed, FocusActivity.Paused));
    }

    public void Resume()
    {
        if (_status == FocusStatus.Paused)
            _status = FocusStatus.Running;
    }

    public void End() => _status = FocusStatus.Ended;

    public void SetDeveloperMode(bool enabled)
    {
        _developerMode = enabled;
        if (enabled)
            _usedDeveloperMode = true;
    }

    public void AddObservation(FocusCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (ShouldMonitor)
            _checks.Add(check);
    }
}

public sealed class NudgePolicy
{
    private readonly List<FocusCheck> _run = new();
    private readonly double _maximumGap;
    private readonly bool _developerMode;
    private double _lastNudge = double.NegativeInfinity;

    public NudgePolicy(double maximumGap = 45, bool developerMode = false)
    {
        _maximumGap = maximumGap;
        _developerMode = developerMode;
    }

    public bool Observe(FocusCheck check, double age)
    {
        if (!double.IsFinite(age) || age < 0 || age > 20 || !double.IsFinite(check.Elapsed) ||
            check.Activity != FocusActivity.OffTask ||
            !double.IsFinite(check.Probability) || !double.IsFinite(check.Confidence) ||
            check.Probability is < 0.5 or > 1 || check.Confidence is < 0 or > 1)
        {
            Reset();
            return false;
        }

        if (_run.Count > 0)
        {
            var last = _run[^1];
            if (check.Elapsed <= last.Elapsed)
            {
                Reset();
                return false;
            }

            if (check.Elapsed - last.Elapsed > _maximumGap)
                Reset();
        }

        _run.Add(check);
        var strength = _run.Average(item => item.Probability);
        var minimum = _developerMode ? 40d : 120d;
        var maximum = _developerMode ? 80d : 240d;
        var required = strength >= 0.95
            ? minimum
            : maximum - Math.Max(0, strength - 0.5) / 0.45 * (maximum - minimum);

        if (_run.Count < 2 ||
            check.Elapsed - _run[0].Elapsed < Math.Max(minimum, required) ||
            check.Elapsed - _lastNudge < 120)
            return false;

        _lastNudge = check.Elapsed;
        Reset();
        return true;
    }

    public void Reset() => _run.Clear();
}

