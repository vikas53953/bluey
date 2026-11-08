namespace Boo.Core;

public sealed class CaptureActivityTracker
{
    private readonly object _gate = new();
    private readonly List<CaptureActivityOperation> _activeOperations = new();

    public IReadOnlyList<DisplayInfo> ActiveDisplays
    {
        get
        {
            lock (_gate)
            {
                if (_activeOperations.Count == 0)
                {
                    return Array.Empty<DisplayInfo>();
                }

                var displays = new List<DisplayInfo>(_activeOperations.Count);
                var displayIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var operation in _activeOperations)
                {
                    var display = operation.Display!;
                    if (displayIds.Add(display.Id))
                    {
                        displays.Add(display);
                    }
                }

                return displays.AsReadOnly();
            }
        }
    }

    public CaptureActivityOperation CreateOperation() => new(this);

    internal void Start(CaptureActivityOperation operation, DisplayInfo display)
    {
        lock (_gate)
        {
            if (operation.IsDisposed)
            {
                throw new ObjectDisposedException(nameof(CaptureActivityOperation));
            }

            if (operation.IsStarted)
            {
                throw new InvalidOperationException("A capture activity operation can only be started once.");
            }

            operation.IsStarted = true;
            operation.Display = display;
            _activeOperations.Add(operation);
        }
    }

    internal void Dispose(CaptureActivityOperation operation)
    {
        lock (_gate)
        {
            if (operation.IsDisposed)
            {
                return;
            }

            operation.IsDisposed = true;
            if (operation.IsStarted)
            {
                _activeOperations.Remove(operation);
            }
        }
    }
}

public sealed class CaptureActivityOperation : IDisposable
{
    private readonly CaptureActivityTracker _tracker;

    internal CaptureActivityOperation(CaptureActivityTracker tracker)
    {
        _tracker = tracker;
    }

    internal bool IsDisposed { get; set; }

    internal bool IsStarted { get; set; }

    internal DisplayInfo? Display { get; set; }

    public void Start(DisplayInfo display)
    {
        ArgumentNullException.ThrowIfNull(display);
        _tracker.Start(this, display);
    }

    public void Dispose() => _tracker.Dispose(this);
}
