using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;

namespace Boo.App;

/// <summary>Opt-in diagnostic timestamps only; enabled by a marker in the isolated QA data folder.</summary>
internal sealed class UiTiming
{
    private static readonly UiTiming Instance = new();
    private readonly ConcurrentQueue<string> _lines = new();
    private string? _path;
    private System.Threading.Timer? _timer;
    public static void Mark(string phase) => Instance.Record(phase);
    private void Record(string phase)
    {
        if (_path is null) return;
        _lines.Enqueue($"{Stopwatch.GetTimestamp()} thread={Environment.CurrentManagedThreadId} {phase}");
    }
    public static void Start(Dispatcher dispatcher, string directory)
    {
        if (!File.Exists(Path.Combine(directory, "qa-ui-timing.enabled"))) return;
        Instance._path = Path.Combine(directory, "ui-timing.txt");
        Instance.Record($"start frequency={Stopwatch.Frequency}");
        dispatcher.Hooks.OperationPosted += (_, e) => Instance.Record($"dispatcher-posted id={e.Operation.GetHashCode()} priority={e.Operation.Priority}");
        dispatcher.Hooks.OperationStarted += (_, e) => Instance.Record($"dispatcher-started id={e.Operation.GetHashCode()} priority={e.Operation.Priority}");
        dispatcher.Hooks.OperationCompleted += (_, e) => Instance.Record($"dispatcher-completed id={e.Operation.GetHashCode()} priority={e.Operation.Priority}");
        Instance._timer = new System.Threading.Timer(_ => Instance.Tick(), null, 50, Timeout.Infinite);
    }
    private void Tick()
    {
        try
        {
            var batch = new List<string>();
            while (_lines.TryDequeue(out var line)) batch.Add(line);
            if (batch.Count > 0) File.AppendAllLines(_path!, batch);
        }
        catch (IOException) { Trace.TraceWarning("UI timing file write failed."); }
        catch (UnauthorizedAccessException) { Trace.TraceWarning("UI timing file access denied."); }
        _timer?.Change(50, Timeout.Infinite);
    }
}
