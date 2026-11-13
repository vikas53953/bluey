using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;

namespace Boo.Explorer;

/// <summary>
/// Counts the HTTP requests a running Boo.exe REALLY sends, at Boo's own network boundary: .NET's built-in
/// "System.Net.Http" event source (RequestStart / RequestStop / RequestFailed), read from outside the process over the
/// standard .NET diagnostics channel (EventPipe). Nothing in Boo changes, no proxy sits in between (the anti-mock guard
/// forbids one), and every path is counted: typed chat, voice, focus checks, research, retries if any existed.
/// TLS hides request bodies, never these events. One meter per Boo process; the totals are static for the whole run.
/// A launch that holds the REAL key is "billable": if its requests would pass <see cref="ExplorerLedger.HardCap"/>,
/// the meter kills that Boo at once (backstop; the harness also refuses to ask past the cap).
/// </summary>
public sealed class RequestMeter : IDisposable
{
    private const int RequestStartId = 1, RequestStopId = 2, RequestFailedId = 3;
    private static readonly ConcurrentDictionary<int, RequestMeter> ByProcess = new();
    private static readonly ConcurrentQueue<string> Log = new();
    private static int s_billableStarted;

    private readonly EventPipeSession _session;
    private readonly Thread _reader;
    private readonly int _pid;
    private readonly bool _billable;
    private int _started, _ended;

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
                    new EventPipeProvider("System.Net.Http", EventLevel.Informational), requestRundown: false);
            }
            catch (Exception) when (clock.Elapsed < TimeSpan.FromSeconds(20))
            {
                Thread.Sleep(250); // the runtime's diagnostics channel opens a moment after start
            }
        }
        _session = session;
        var source = new EventPipeEventSource(_session.EventStream);
        source.Dynamic.All += OnEvent;
        _reader = new Thread(() =>
        {
            try { source.Process(); }
            catch (Exception) { /* the process exited or the session stopped */ }
        }) { IsBackground = true, Name = "request-meter-" + pid };
        _reader.Start();
        Note($"attached to {launch} (pid {pid}, billable={billable})");
    }

    public string Launch { get; }

    /// <summary>Requests started by launches that hold the real key: the number that is billed, at most.</summary>
    public static int BillableStarted => Volatile.Read(ref s_billableStarted);

    public static IReadOnlyCollection<string> Events => Log.ToArray();

    /// <summary>Requests this Boo has started and not yet finished or failed.</summary>
    public int InFlight => Volatile.Read(ref _started) - Volatile.Read(ref _ended);

    public int Started => Volatile.Read(ref _started);

    /// <summary>Attaches once per Boo process. Fails closed: a billable launch without a meter may not ask anything.</summary>
    public static RequestMeter For(int pid, string launch, bool billable) =>
        ByProcess.GetOrAdd(pid, _ => new RequestMeter(pid, launch, billable));

    private void OnEvent(TraceEvent e)
    {
        if (e.ProviderName != "System.Net.Http") return;
        switch ((int)e.ID)
        {
            case RequestStartId:
                var host = e.PayloadByName("host") as string ?? "?";
                var path = e.PayloadByName("pathAndQuery") as string ?? "";
                Interlocked.Increment(ref _started);
                var n = _billable ? Interlocked.Increment(ref s_billableStarted) : 0;
                Note($"START  {Launch}: {host}{TrimQuery(path)}" + (_billable ? $"  [billable #{n}]" : ""));
                if (_billable && n > ExplorerLedger.HardCap)
                {
                    Note($"BUDGET EXCEEDED: billable request #{n} > cap {ExplorerLedger.HardCap}; killing Boo (pid {_pid})");
                    try { Process.GetProcessById(_pid).Kill(); } catch (Exception) { }
                }
                break;
            case RequestStopId:
                Interlocked.Increment(ref _ended);
                Note($"STOP   {Launch}: HTTP {e.PayloadByName("statusCode")}");
                break;
            case RequestFailedId:
                Interlocked.Increment(ref _ended);
                Note($"FAILED {Launch}: {e.PayloadByName("exceptionMessage")}");
                break;
        }
        ExplorerLedger.Flush();
    }

    private static string TrimQuery(string path) => path.Split('?')[0];

    private static void Note(string text) => Log.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} {text}");

    public void Dispose()
    {
        try { _session.Stop(); } catch (Exception) { }
        try { _session.Dispose(); } catch (Exception) { }
        _reader.Join(2000);
    }
}
