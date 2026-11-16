using System.ComponentModel;
using System.IO;
using System.Windows.Controls;
using Boo.Core;
using Boo.Storage;

namespace Boo.App;

/// <summary>
/// Focus history persistence off the UI thread (issue #168). A focus snapshot is written to sessions.db, and the history
/// list re-read, on one ordered background queue, so a slow or locked disk never freezes Boo's face, panel or UI
/// Automation answers. Writes keep their order.
/// A status line that promises a saved state ("Paused ...", "Running ...", "Session complete ... saved") is an
/// acknowledgment of one specific save: it is shown only when that save has succeeded, and an honest "Not saved: ..."
/// line is shown when it failed or when no history store was available. Acknowledgments are numbered; only the newest
/// may change the status, and any other write to the focus status makes every pending acknowledgment stale, so an
/// older action can never overwrite a newer one.
/// Exit waits for the queue, bounded at 10 s: a save still blocked after that is not guaranteed to reach the disk.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan FocusHistoryFlushTimeout = TimeSpan.FromSeconds(10);
    private readonly object _focusHistoryGate = new();
    private Task _focusHistoryTail = Task.CompletedTask;
    private long _focusStatusGeneration;
    private long _focusStatusNewest;
    private bool _applyingFocusAcknowledgment;
    private bool _focusStatusWatched;

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
        return EnqueueFocusHistoryWork(() =>
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

        _ = EnqueueFocusHistoryWork(() => ReadHistoryRows(store));
    }

    /// <summary>
    /// Acknowledges <paramref name="save"/>: when it has finished, shows <paramref name="saved"/> if it succeeded and
    /// <paramref name="unsaved"/> if it did not, unless a newer acknowledgment or any other focus status has been shown
    /// since this one was requested.
    /// </summary>
    private void ShowFocusStatusWhenSaved(Task<bool> save, string saved, string unsaved)
    {
        WatchFocusStatus();
        var generation = ++_focusStatusGeneration;
        _focusStatusNewest = generation;
        _ = AcknowledgeAsync(save, generation, saved, unsaved);
    }

    private async Task AcknowledgeAsync(Task<bool> save, long generation, string saved, string unsaved)
    {
        var ok = await save.ConfigureAwait(false);
        OnUiThread(() => ApplyFocusAcknowledgment(generation, ok ? saved : unsaved));
    }

    private void ApplyFocusAcknowledgment(long generation, string text)
    {
        if (generation != _focusStatusNewest)
        {
            return;
        }

        _applyingFocusAcknowledgment = true;
        try
        {
            FocusStatus.Text = text;
        }
        finally
        {
            _applyingFocusAcknowledgment = false;
        }
    }

    /// <summary>Any focus status written outside an acknowledgment is newer than every pending acknowledgment.</summary>
    private void WatchFocusStatus()
    {
        if (_focusStatusWatched)
        {
            return;
        }

        _focusStatusWatched = true;
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))
            .AddValueChanged(FocusStatus, (_, _) =>
            {
                if (!_applyingFocusAcknowledgment)
                {
                    _focusStatusNewest = ++_focusStatusGeneration;
                }
            });
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

    private Task<bool> EnqueueFocusHistoryWork(Func<IReadOnlyList<FocusHistoryRow>> work)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_focusHistoryGate)
        {
            _focusHistoryTail = _focusHistoryTail.ContinueWith(
                _ => done.TrySetResult(RunFocusHistoryWork(work)),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        return done.Task;
    }

    /// <summary>Runs one queued step; true when it succeeded. The history list is updated (or marked unavailable) first.</summary>
    private bool RunFocusHistoryWork(Func<IReadOnlyList<FocusHistoryRow>> work)
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

        var ok = !failed && rows is not null;
        OnUiThread(() =>
        {
            if (ok)
            {
                ApplyHistoryRows(rows!);
            }
            else
            {
                _sessionStore = null;
                SetHistoryUnavailable();
            }
        });
        return ok;
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
