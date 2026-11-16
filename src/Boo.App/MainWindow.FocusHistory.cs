using System.IO;
using Boo.Core;
using Boo.Storage;

namespace Boo.App;

/// <summary>
/// Focus history persistence off the UI thread (issue #168). A focus snapshot is written to sessions.db, and the history
/// list re-read, on one ordered background queue, so a slow or locked disk never freezes Boo's face, panel or UI
/// Automation answers. Writes keep their order. A status line that promises a saved state ("Paused ...", "Running ...")
/// is shown only when every write queued before it has finished, so a status on screen still means the row is on disk.
/// Exit waits for the queue (bounded).
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan FocusHistoryFlushTimeout = TimeSpan.FromSeconds(10);
    private readonly object _focusHistoryGate = new();
    private Task _focusHistoryTail = Task.CompletedTask;

    /// <summary>Queues a save of the focus snapshot (and a history refresh); never touches the disk on the UI thread.</summary>
    private void SaveFocusSnapshot(FocusSessionController? controller = null)
    {
        controller ??= _focus;
        var store = _sessionStore;
        if (controller is null || store is null)
        {
            return;
        }

        var snapshot = controller.Snapshot;
        _lastFocusSaveRealElapsed = snapshot.RealElapsed;
        EnqueueFocusHistoryWork(() =>
        {
            store.Save(snapshot);
            return ReadHistoryRows(store);
        });
    }

    /// <summary>Re-reads the history list on the background queue, after any pending save.</summary>
    private void RefreshHistoryInBackground()
    {
        var store = _sessionStore;
        if (store is null)
        {
            SetHistoryUnavailable();
            return;
        }

        EnqueueFocusHistoryWork(() => ReadHistoryRows(store));
    }

    /// <summary>
    /// Shows <paramref name="text"/> once every save queued so far has finished. If anything else has changed the
    /// focus status in the meantime, the newer text stays.
    /// </summary>
    private void ShowFocusStatusWhenSaved(string text)
    {
        var shown = FocusStatus.Text;
        Enqueue(() => OnUiThread(() =>
        {
            if (string.Equals(FocusStatus.Text, shown, StringComparison.Ordinal))
            {
                FocusStatus.Text = text;
            }
        }));
    }

    /// <summary>Waits (bounded) for every queued save; called when Boo exits.</summary>
    private void FlushFocusHistory()
    {
        Task tail;
        lock (_focusHistoryGate)
        {
            tail = _focusHistoryTail;
        }

        tail.Wait(FocusHistoryFlushTimeout);
    }

    private void EnqueueFocusHistoryWork(Func<IReadOnlyList<FocusHistoryRow>> work) =>
        Enqueue(() => RunFocusHistoryWork(work));

    private void Enqueue(Action step)
    {
        lock (_focusHistoryGate)
        {
            _focusHistoryTail = _focusHistoryTail.ContinueWith(
                _ => step(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private void RunFocusHistoryWork(Func<IReadOnlyList<FocusHistoryRow>> work)
    {
        IReadOnlyList<FocusHistoryRow>? rows = null;
        var failed = false;
        try
        {
            rows = work();
        }
        catch (InvalidDataException)
        {
            failed = true;
        }
        catch (IOException)
        {
            failed = true;
        }
        catch (UnauthorizedAccessException)
        {
            failed = true;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            failed = true;
        }
        catch (ArgumentException)
        {
            failed = true;
        }
        catch (OverflowException)
        {
            failed = true;
        }

        OnUiThread(() =>
        {
            if (failed || rows is null)
            {
                _sessionStore = null;
                SetHistoryUnavailable();
            }
            else
            {
                ApplyHistoryRows(rows);
            }
        });
    }

    /// <summary>
    /// Posts a UI update from the queue's worker. The worker does not wait for it (exit waits for the queue on the UI
    /// thread), so updates keep the order they were posted in; a failure is written to the trace, not lost.
    /// </summary>
    private void OnUiThread(Action update) =>
        _ = Dispatcher.InvokeAsync(update).Task.ContinueWith(
            task => System.Diagnostics.Trace.TraceError($"Boo focus history UI update failed: {task.Exception?.GetBaseException().GetType().Name}"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    private static IReadOnlyList<FocusHistoryRow> ReadHistoryRows(SessionStore store) =>
        store.List()
            .Take(20)
            .Select(session => FocusTimeline.ToHistoryRow(session, HistoryBarWidth))
            .ToList();

    private void ApplyHistoryRows(IReadOnlyList<FocusHistoryRow> rows)
    {
        HistoryList.ItemsSource = rows;
        HistoryStatus.Text = rows.Count == 0
            ? "No saved sessions. Unknown gaps and pauses remain distinct when a session is recorded."
            : "Periodic observations are estimates; unknown gaps and pauses are not counted as exact active time.";
    }
}
