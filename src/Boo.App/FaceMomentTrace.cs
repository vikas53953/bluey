using System.Diagnostics;

namespace Boo.App;

internal readonly record struct FaceTraceContext(long Invocation, long HappyGeneration, long Arm);
internal readonly record struct FaceTraceRecord(FaceTraceContext Context, string Phase, long Begin, long End, long Tick, long Value, long Flags);

/// <summary>Opt-in producer correlation only; no scheduling or face-state decisions live here.</summary>
internal sealed class FaceMomentTrace
{
    private long _invocation;
    private long _generation;
    private long _arm;
    private bool _happyActive;
    public static bool Enabled => UiTiming.ProducerEnabled;
    public static long Stamp() => Enabled ? Stopwatch.GetTimestamp() : 0;
    public static FaceTraceContext Current => System.Windows.Application.Current is App app ? app.ProducerContext : default;
    public FaceTraceContext Context => new(_invocation, _generation, _arm);

    public Scope? Begin(bool newHappyHold)
    {
        if (!Enabled) return null;
        _invocation++;
        if (newHappyHold)
        {
            var prior = _generation;
            var active = _happyActive;
            _generation++;
            _happyActive = true;
            var at = Stamp();
            Write("happy-hold-generation", at, at, value: prior, flags: active ? 1 : 0);
        }
        return new Scope(this);
    }
    public long Arm()
    {
        if (!Enabled) return 0;
        _arm++;
        ((App)System.Windows.Application.Current).ProducerContext = Context;
        return _arm;
    }
    public void ClearHappy(bool clearing)
    {
        if (!Enabled || !clearing) return;
        var at = Stamp();
        Write("happy-clear", at, at, flags: _happyActive ? 1 : 0);
        _happyActive = false;
    }
    public void Write(string phase, long begin, long end, long tick = 0, long value = 0, long flags = 0)
    {
        if (Enabled) Write(Context, phase, begin, end, tick, value, flags);
    }
    public static void Write(FaceTraceContext context, string phase, long begin, long end, long tick = 0, long value = 0, long flags = 0)
    {
        if (Enabled) UiTiming.Producer(new FaceTraceRecord(context, phase, begin, end, tick, value, flags));
    }
    public sealed class Scope : IDisposable
    {
        private readonly FaceTraceContext _previous;
        private readonly App _app;
        internal Scope(FaceMomentTrace owner)
        {
            _app = (App)System.Windows.Application.Current;
            _previous = _app.ProducerContext;
            _app.ProducerContext = owner.Context;
        }
        public void Dispose() => _app.ProducerContext = _previous;
    }
}
