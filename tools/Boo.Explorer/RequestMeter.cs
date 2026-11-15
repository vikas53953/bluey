using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;

namespace Boo.Explorer;

/// <summary>
/// Watches the HTTP requests a running Boo.exe REALLY starts, at Boo's own network boundary: .NET's built-in
/// "System.Net.Http" event source (RequestStart / RequestStop / RequestFailed), read from outside the process over the
/// standard .NET diagnostics channel (EventPipe). Nothing in Boo changes and no proxy sits in between (the anti-mock
/// guard forbids one). TLS hides request bodies, never these events.
///
/// What it covers: every HTTP request Boo starts (typed chat, research, focus checks, update checks) AND the opening of
/// a voice session, because the realtime WebSocket is opened with an HTTP request. What it does NOT see: voice turns sent
/// over an already-open WebSocket. The explorer therefore keeps voice off (speech off in every launch, checked before
/// each ask), and any request it did not admit (such as a voice session opening) ends the run.
///
/// The meter is the CHECK, not the gate: the gate is admission (<see cref="ExplorerLedger.Admit"/>). Each admitted ask
/// allows exactly one request on that Boo; a request beyond what was admitted is UNADMITTED, and the meter kills that
/// Boo at once and marks the run failed. Because events arrive asynchronously, a misbehaving Boo could start a few more
/// requests before the kill: the guarantee is "at most the admitted count, plus what one surprise burst sends before it
/// is seen", and every such request is listed.
///
/// Delivery: the 1 s System.Net.Http counters make EventPipe deliver promptly (without them events arrived only when the
/// next event came, up to 15 s late, run 43485951548). A failed request raises RequestFailed AND RequestStop(-1): only
/// RequestStop ends a request. Fails closed: a reader error or lost events set <see cref="Problem"/>, and no ask runs
/// on a meter with a problem.
/// </summary>
public sealed class RequestMeter : IDisposable
{
    private const int RequestStartId = 1, RequestStopId = 2, RequestFailedId = 3;
    private static readonly ConcurrentDictionary<int, RequestMeter> ByProcess = new();
    private static readonly ConcurrentQueue<string> Log = new();
    private static int s_billableStarted;

    private readonly EventPipeSession _session;
    private readonly EventPipeEventSource _source;
    private readonly Thread _reader;
    private readonly int _pid;
    private readonly bool _billable;
    private int _started, _stopped, _admitted;
    private volatile string? _problem;
    private volatile bool _stopping;

    private RequestMeter(int pid, string launch, bool billable)
    {
        _pid = pid;
        _billable = billable;
        Launch = launch;
        var client = new DiagnosticsClient(pid);
        EventPipeSession? session = null;
        var clock = Stopwatch.StartNew();
        while (session is null)
        {
            try
            {
                session = client.StartEventPipeSession(
                    new EventPipeProvider("System.Net.Http", EventLevel.Informational, -1,
                        new Dictionary<string, string> { ["EventCounterIntervalSec"] = "1" }),
                    requestRundown: false);
            }
            catch (Exception) when (clock.Elapsed < TimeSpan.FromSeconds(20))
            {
                Thread.Sleep(250); // the runtime's diagnostics channel opens a moment after start
            }
        }
        _session = session;
        _source = new EventPipeEventSource(_session.EventStream);
        _source.Dynamic.All += OnEvent;
        _reader = new Thread(() =>
        {
            try
            {
                _source.Process();
            }
            catch (Exception ex) when (!_stopping && !ProcessGone())
            {
                _problem = $"meter reader failed while Boo was running: {ex.GetType().Name}: {ex.Message}";
                Note(DateTime.UtcNow, "METER FAILED " + Launch + ": " + _problem);
            }
            catch (Exception)
            {
                // Boo exited (killed on purpose or by a crash the health check reports) or the session was stopped.
            }
        }) { IsBackground = true, Name = "request-meter-" + pid };
        _reader.Start();
        Note(DateTime.UtcNow, $"attached to {launch} (pid {pid}, billable={billable})");
    }

    public string Launch { get; }

    /// <summary>Requests started by launches that hold the real key, across the whole run.</summary>
    public static int BillableStarted => Volatile.Read(ref s_billableStarted);

    public static IReadOnlyCollection<string> Events => Log.ToArray();

    /// <summary>Every meter's problem, if any (reader failed, events lost, an unadmitted request).</summary>
    public static IEnumerable<string> Problems => ByProcess.Values.Select(m => m.Problem).OfType<string>();

    /// <summary>Why this meter can no longer be trusted, or null.</summary>
    public string? Problem => _problem ?? (_source.EventsLost > 0 ? $"EventPipe lost {_source.EventsLost} event(s) for {Launch}" : null);

    public int Started => Volatile.Read(ref _started);

    /// <summary>Started minus stopped, from the request events themselves (not the 1 s counter).</summary>
    public int InFlight => Volatile.Read(ref _started) - Volatile.Read(ref _stopped);

    /// <summary>Attaches once per Boo process.</summary>
    public static RequestMeter For(int pid, string launch, bool billable) =>
        ByProcess.GetOrAdd(pid, _ => new RequestMeter(pid, launch, billable));

    /// <summary>Allows exactly one more request on this Boo (called by the ledger when it admits an ask).</summary>
    public void AllowOne() => Interlocked.Increment(ref _admitted);

    /// <summary>Waits until every request this Boo started has stopped (and its STOP reached the log).</summary>
    public bool Drain(TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (InFlight <= 0 || ProcessGone()) return InFlight <= 0;
            Thread.Sleep(100);
        }
        return false;
    }

    private bool ProcessGone()
    {
        try { return Process.GetProcessById(_pid).HasExited; }
        catch (ArgumentException) { return true; }
    }

    private void OnEvent(TraceEvent e)
    {
        if (e.ProviderName != "System.Net.Http") return;
        if (e.EventName == "EventCounters") return; // they exist to make delivery prompt; counts come from the events
        var at = e.TimeStamp.ToUniversalTime();
        switch ((int)e.ID)
        {
            case RequestStartId:
                var host = e.PayloadByName("host") as string ?? "?";
                var path = e.PayloadByName("pathAndQuery") as string ?? "";
                var mine = Interlocked.Increment(ref _started);
                var n = _billable ? Interlocked.Increment(ref s_billableStarted) : 0;
                Note(at, $"START  {Launch}: {host}{TrimQuery(path)}" + (_billable ? $"  [real-key #{n}]" : ""));
                if (_billable && mine > Volatile.Read(ref _admitted))
                {
                    _problem = $"UNADMITTED request #{mine} on {Launch} ({host}{TrimQuery(path)}): only {_admitted} admitted; Boo killed";
                    Note(at, _problem);
                    try { Process.GetProcessById(_pid).Kill(); } catch (Exception) { }
                }
                break;
            case RequestStopId:
                Interlocked.Increment(ref _stopped); // raised for every request, failed ones too (status -1)
                Note(at, $"STOP   {Launch}: HTTP {e.PayloadByName("statusCode")}");
                break;
            case RequestFailedId:
                Note(at, $"FAILED {Launch}: {e.PayloadByName("exceptionMessage")}");
                break;
        }
        ExplorerLedger.Flush();
    }

    private static string TrimQuery(string path) => path.Split('?')[0];

    /// <summary>Logs with the EVENT's own time (when Boo raised it), not when the harness received it.</summary>
    private static void Note(DateTime at, string text) => Log.Enqueue($"{at:HH:mm:ss.fff} {text}");

    /// <summary>Stops every session and waits for its reader, so the final ledger holds every delivered event.</summary>
    public static void DrainAll()
    {
        foreach (var meter in ByProcess.Values) meter.Dispose();
        ExplorerLedger.Flush();
    }

    public void Dispose()
    {
        _stopping = true;
        try { _session.Stop(); } catch (Exception) { }
        _reader.Join(5000);
        try { _session.Dispose(); } catch (Exception) { }
    }
}
