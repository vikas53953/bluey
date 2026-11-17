using System.IO;
using Boo.Core;
using Boo.Storage;

namespace Boo.App;

/// <summary>
/// Focus history persistence off the UI thread (issue #168). A focus snapshot is written to sessions.db, and the history
/// list re-read, on one ordered background queue, so a slow or locked disk never freezes Boo's face, panel or UI
/// Automation answers. Writes keep their order.
/// A status line that promises a saved state ("Paused ...", "Running ...", "Session complete ... saved") is an
/// acknowledgment of one specific save: it is shown only when that save has succeeded, and an honest "Not saved: ..."
/// line is shown when it failed or when no history store was available. A write that committed is acknowledged as saved
/// even if the history list could not be re-read afterwards (the list then says history is unavailable). Acknowledgments
/// are numbered; only the newest may change the status, and every other focus-status write (all go through
/// <see cref="SetFocusStatus"/>, even one that repeats the text already shown) makes every pending acknowledgment stale,
/// so an older action can never overwrite a newer one.
/// Exit waits for the queue, bounded at 10 s: a save still blocked after that is not guaranteed to reach the disk.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan FocusHistoryFlushTimeout = TimeSpan.FromSeconds(10);
    private readonly object _focusHistoryGate = new();
    private Task _focusHistoryTail = Task.CompletedTask;
    private long _focusStatusGeneration;
    private long _focusStatusNewest;

    /// <summary>Queues a save of the focus snapshot (and a history refresh); never touches the disk on the UI thread.</summary>
    private void SaveFocusSnapshot(FocusSessionController? controller = null) => _ = SaveFocusSnapshotTracked(controller);

    /// <summary>
    /// Queues a save like <see cref="SaveFocusSnapshot"/>. The task completes with true when this snapshot is on disk,
    /// false when it was not saved (no store, or the write failed); a status that promises a saved state waits on it.
    /// </summary>
    private Task<bool> SaveFocusSnapshotTracked(FocusSessionController? controller = null)
    {
        controller ??= _focus;
        var store = _sessionStore;
        if (controller is null || store is null)
        {
            return Task.FromResult(false);
        }

        var snapshot = controller.Snapshot;
        _lastFocusSaveRealElapsed = snapshot.RealElapsed;
        return EnqueueFocusHistoryWork(store, () => store.Save(snapshot));
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

        _ = EnqueueFocusHistoryWork(store, write: null);
    }

    /// <summary>
    /// Acknowledges <paramref name="save"/>: when it has finished, shows <paramref name="saved"/> if it succeeded and
    /// <paramref name="unsaved"/> if it did not, unless a newer acknowledgment or any other focus status has been shown
    /// since this one was requested.
    /// </summary>
    private void ShowFocusStatusWhenSaved(Task<bool> save, string saved, string unsaved)
    {
        var generation = ++_focusStatusGeneration;
        _focusStatusNewest = generation;
        _ = AcknowledgeAsync(save, generation, saved, unsaved);
    }

    private async Task AcknowledgeAsync(Task<bool> save, long generation, string saved, string unsaved)
    {
        var ok = await save.ConfigureAwait(false);
        OnUiThread(() => ApplyFocusAcknowledgment(generation, ok ? saved : unsaved));
    }

    /// <summary>
    /// The one way to write the focus status outside an acknowledgment: every call, even one that repeats the text
    /// already shown, is a newer action and makes every pending acknowledgment stale.
    /// </summary>
    private void SetFocusStatus(string text)
    {
        _focusStatusNewest = ++_focusStatusGeneration;
        FocusStatus.Text = text;
    }

    private void ApplyFocusAcknowledgment(long generation, string text)
    {
        if (generation == _focusStatusNewest)
        {
            FocusStatus.Text = text;
        }
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

    /// <summary>
    /// Queues an optional write and then a re-read of the history list. The task completes with true when the write
    /// committed (or there was none), false when it failed. A failed re-read after a committed write does not make the
    /// write unsaved: the history list just says it is unavailable. The task completes on the UI thread, inside the same
    /// step that puts the history rows on screen and after them, so an acknowledgment that waits on it can never be shown
    /// before the history it promises, whatever priority that step runs at.
    /// </summary>
    private Task<bool> EnqueueFocusHistoryWork(SessionStore store, Action? write)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_focusHistoryGate)
        {
            _focusHistoryTail = _focusHistoryTail.ContinueWith(
                _ => RunFocusHistoryWork(store, write, done),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        return done.Task;
    }

    private void RunFocusHistoryWork(SessionStore store, Action? write, TaskCompletionSource<bool> done)
    {
        var written = write is null || TryStoreStep(() => { write(); return true; });
        var rows = written ? TryStoreStep(() => ReadHistoryRows(store)) : null;
        OnUiThread(() =>
        {
            if (!written)
            {
                _sessionStore = null;
                SetHistoryUnavailable();
            }
            else if (rows is null)
            {
                SetHistoryUnavailable();
            }
            else
            {
                ApplyHistoryRows(rows);
            }

            done.TrySetResult(written);
        });
    }

    /// <summary>Runs one sessions.db step; the store's known failures return the default instead of throwing.</summary>
    private static T? TryStoreStep<T>(Func<T> step)
    {
        try
        {
            return step();
        }
        catch (InvalidDataException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
        }
        catch (ArgumentException)
        {
        }
        catch (OverflowException)
        {
        }

        return default;
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
