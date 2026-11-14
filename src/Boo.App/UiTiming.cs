using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace Boo.App;

/// <summary>Opt-in buffered diagnostics. The narrow marker never registers dispatcher hooks.</summary>
internal sealed class UiTiming
{
    private static readonly UiTiming Instance = new();
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly ConcurrentQueue<(long Id, FaceTraceRecord Record)> _producer = new();
    private readonly object _writerGate = new();
    private string? _path;
    private string? _directory;
    private bool _broad;
    private long _issued;
    private long _written;
    private long _dropped;
    private long _writeFailures;
    private string? _acknowledged;
    private string? _requested;
    private long _drainCutoff;
    private System.Threading.Timer? _timer;
    private bool _producerEnabled;
    public static bool ProducerEnabled => Instance._producerEnabled;
    public static void Mark(string phase) { if (Instance._broad) Instance.Record(phase); }
    private void Record(string phase) => _lines.Enqueue($"{Stopwatch.GetTimestamp()} thread={Environment.CurrentManagedThreadId} {phase}");
    public static void Producer(FaceTraceRecord record)
    {
        if (!ProducerEnabled) return;
        var id = Interlocked.Increment(ref Instance._issued);
        if (id > 50000) { Interlocked.Increment(ref Instance._dropped); return; }
        Instance._producer.Enqueue((id, record));
    }
    public static void Start(Dispatcher dispatcher, string directory)
    {
        var broad = File.Exists(Path.Combine(directory, "qa-ui-timing.enabled"));
        var narrow = File.Exists(Path.Combine(directory, "qa-face-timing.enabled"));
        if (!broad && !narrow) return;
        Instance._directory = directory;
        Instance._broad = broad;
        Instance._producerEnabled = narrow;
        Instance._path = Path.Combine(directory, broad ? "ui-timing.txt" : "face-moment-timing.txt");
        Instance.Record($"start frequency={Stopwatch.Frequency} broad={broad} producer={narrow}");
        if (broad)
        {
            dispatcher.Hooks.OperationPosted += (_, e) => Instance.Record($"dispatcher-posted id={e.Operation.GetHashCode()} priority={e.Operation.Priority}");
            dispatcher.Hooks.OperationStarted += (_, e) => Instance.Record($"dispatcher-started id={e.Operation.GetHashCode()} priority={e.Operation.Priority}");
            dispatcher.Hooks.OperationCompleted += (_, e) => Instance.Record($"dispatcher-completed id={e.Operation.GetHashCode()} priority={e.Operation.Priority}");
        }
        Instance._timer = new System.Threading.Timer(_ => Instance.Tick(), null, 50, Timeout.Infinite);
    }
    private void Tick()
    {
        lock (_writerGate)
        {
            var uncommittedProducerCount = 0;
            try
            {
                var batch = new List<string>();
                while (_lines.TryDequeue(out var line)) batch.Add(line);
                while (_producer.TryDequeue(out var item))
                {
                    uncommittedProducerCount++;
                    var r = item.Record;
                    batch.Add($"record={item.Id} invoke={r.Context.Invocation} happy={r.Context.HappyGeneration} arm={r.Context.Arm} phase={r.Phase} qpc=[{r.Begin},{r.End}] tick={r.Tick} value={r.Value} flags={r.Flags}");
                }
                if (batch.Count > 0) File.AppendAllLines(_path!, batch);
                _written += uncommittedProducerCount;
                uncommittedProducerCount = 0;
                DrainReceipt();
            }
            catch (IOException) { Interlocked.Increment(ref _writeFailures); Interlocked.Add(ref _dropped, uncommittedProducerCount); }
            catch (UnauthorizedAccessException) { Interlocked.Increment(ref _writeFailures); Interlocked.Add(ref _dropped, uncommittedProducerCount); }
            _timer?.Change(50, Timeout.Infinite);
        }
    }
    private void DrainReceipt()
    {
        if (!ProducerEnabled) return;
        var requestPath = Path.Combine(_directory!, "qa-face-timing.drain");
        if (!File.Exists(requestPath)) return;
        var request = File.ReadAllText(requestPath).Trim();
        if (!Guid.TryParseExact(request, "N", out _) || request == _acknowledged) return;
        if (request != _requested)
        {
            _requested = request;
            _drainCutoff = Interlocked.Read(ref _issued);
        }
        var cutoff = _drainCutoff;
        if (_written < cutoff && _dropped == 0 && _writeFailures == 0) return;
        var receipt = new { request, cutoff, written = _written, dropped = _dropped, writeFailures = _writeFailures,
            pending = _producer.Count, completeThroughCutoff = _written >= cutoff && _dropped == 0 && _writeFailures == 0,
            incompleteTail = !_producer.IsEmpty, futureEventsNotCovered = true };
        var receiptPath = Path.Combine(_directory!, "qa-face-timing.drained.json");
        File.WriteAllText(receiptPath + ".tmp", JsonSerializer.Serialize(receipt));
        File.Move(receiptPath + ".tmp", receiptPath, true);
        _acknowledged = request;
    }
}
