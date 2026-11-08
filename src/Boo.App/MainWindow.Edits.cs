using System.Windows;
using Boo.Core;
using Boo.Providers;

namespace Boo.App;

/// <summary>
/// Edit -> Apply -> Undo glue (plan unit U5). Every decision lives in Boo.Core
/// (<see cref="UndoJournal"/>, <see cref="ActionReadback"/>, <see cref="ResearchSources"/>);
/// this file only re-reads controls through the existing observation path and routes
/// Undo steps through the existing preview/approval queue. Nothing here executes an
/// action on its own.
/// </summary>
public partial class MainWindow
{
    private static readonly IReadOnlySet<string> UndoTools =
        new HashSet<string>(["click", "type_text"], StringComparer.Ordinal);

    private readonly UndoJournal _undoJournal = new();
    private ActiveUndo? _activeUndo;
    private bool _journalAwaitingApply;

    private void RenderSources(AssistantReply reply, bool research)
    {
        SourceLinksList.Children.Clear();
        IReadOnlyList<SourceLink> sources = reply.Sources;
        if (research)
        {
            var result = ResearchSources.Finalize(reply.Text, reply.Sources);
            sources = result.Sources;
            SourceStatusText.Text = result.SourceStatus;
            SourceStatusText.Visibility = Visibility.Visible;
        }
        else
        {
            ClearSourceStatus();
        }

        foreach (var source in sources)
        {
            AppendSourceLink(source);
        }
    }

    private void ClearSourceStatus()
    {
        SourceStatusText.Text = string.Empty;
        SourceStatusText.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Handles "undo" locally: no model call. Reads the current controls, asks the
    /// journal for a plan, and shows its steps through the normal approval queue.
    /// </summary>
    private async Task HandleUndoRequestAsync(CapturedFrame? frame, RequestLease lease)
    {
        _activeUndo = null;
        if (!_undoJournal.HasPendingUndo)
        {
            ReportUndo("Nothing to undo: Boo has no recorded edit.");
            return;
        }

        if (frame is null)
        {
            ReportUndo("Boo needs a current view of the edited window before it can undo. Nothing was changed.");
            return;
        }

        var controls = await ReadRequestedControlsAsync(frame, lease);
        if (controls is null || !IsCurrent(lease.Generation) || !ReferenceEquals(_requestLease, lease))
        {
            return;
        }

        if (controls.Controls.Count == 0)
        {
            ReportUndo("Boo could not read the window's controls, so it cannot plan an Undo. Nothing was changed.");
            return;
        }

        var plan = _undoJournal.Plan(controls, lease.Generation);
        if (!plan.CanUndo || plan.Steps.Count == 0)
        {
            ReportUndo(plan.BlockedReason ?? "Boo could not plan an Undo for the recorded edit.");
            return;
        }

        var authority = new TaskAuthority(lease.Generation, ExplicitUserTask: true, UndoTools, _settings.FileRoot);
        _activeUndo = new ActiveUndo(plan, lease.Generation, controls.Context);
        _pendingControls = controls;
        _pendingActions.Clear();
        foreach (var step in plan.Steps.Skip(1))
        {
            _pendingActions.Enqueue(step);
        }

        AppendTranscript("Boo", $"Undo is ready: {plan.Steps.Count} step(s) to review before anything changes.");
        ShowActionPreview(plan.Steps[0], authority, controls.Context.Target,
            plan.Steps.Count == 1 ? "Undo: " : $"Undo step 1 of {plan.Steps.Count}: ");
    }

    /// <summary>
    /// A spoken "undo" is handled locally too: the model's reply to that turn is
    /// cancelled and the current voice lease is used for capture and the plan.
    /// </summary>
    private async Task HandleVoiceUndoAsync(RealtimeClient realtime)
    {
        var lease = _requestLease;
        if (lease is null || !IsCurrent(lease.Generation) || !ReferenceEquals(_realtime, realtime))
        {
            return;
        }

        try
        {
            await realtime.InterruptAsync(lease.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The local Undo plan does not depend on the model acknowledging the cancel.
        }

        var activity = _captureActivities.CreateOperation();
        try
        {
            var frame = await CaptureForRequestAsync(lease, activity);
            if (frame is null)
            {
                EndCaptureActivity(activity);
            }

            if (!IsCurrent(lease.Generation) || !ReferenceEquals(_requestLease, lease)
                || !ReferenceEquals(_realtime, realtime))
            {
                return;
            }

            await HandleUndoRequestAsync(frame, lease);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            EndCaptureActivity(activity);
        }
    }

    /// <summary>
    /// Runs after an approved action returned. Records verified edits, re-reads the
    /// target's controls after a click (and between queued steps), and replaces the
    /// click's unverified outcome with what the re-read actually observed. A click
    /// in the edited window that cannot be tied to the recorded edits clears the
    /// journal, so a later "undo" never reports a false restore.
    /// </summary>
    private async Task<ToolOutcome> ReadBackActionAsync(
        ToolRequest action, ToolOutcome outcome, ControlObservation? before, WindowIdentity? target, RequestLease lease)
    {
        if (!outcome.Committed || action.Name is not ("click" or "type_text"))
        {
            return outcome;
        }

        var undo = _activeUndo is { } active && active.Generation == action.Generation ? active : null;
        action.Arguments.TryGetValue("automationId", out var automationId);

        if (undo is not null)
        {
            return await ReadBackUndoStepAsync(undo, outcome, before, lease);
        }

        if (action.Name == "type_text")
        {
            if (before is not null && outcome.Success && !string.IsNullOrWhiteSpace(automationId)
                && action.Arguments.TryGetValue("text", out var typed) && typed is not null)
            {
                var field = before.Controls.FirstOrDefault(
                    control => string.Equals(control.AutomationId, automationId, StringComparison.Ordinal));
                _undoJournal.RecordEdit(before.Context.Target, automationId, field?.Value, typed);
                _journalAwaitingApply = true;
            }

            if (before is not null && _pendingActions.Count > 0)
            {
                var between = await PollControlsAsync(before.Context, lease, _ => true);
                AdvancePendingControls(before, between);
            }

            return outcome;
        }

        // Click.
        var window = before?.Context.Target ?? target;
        var applyRecorded = false;
        try
        {
            if (before is null || string.IsNullOrWhiteSpace(automationId))
            {
                return outcome;
            }

            var after = await PollControlsAsync(before.Context, lease,
                observation => ControlDiff.Compare(before, observation).Count > 0);
            AdvancePendingControls(before, after);
            if (after is null)
            {
                return outcome;
            }

            var verified = ActionReadback.VerifyClick(before, after);
            if (verified.Success && _journalAwaitingApply
                && SameWindow(_undoJournal.Window, before.Context.Target))
            {
                _undoJournal.RecordApply(before.Context.Target, automationId, ControlDiff.Compare(before, after));
                _journalAwaitingApply = false;
                applyRecorded = true;
            }

            return verified;
        }
        finally
        {
            if (!applyRecorded && _undoJournal.HasPendingUndo
                && (window is null || SameWindow(_undoJournal.Window, window)))
            {
                // A committed click in the edited window that Boo could not tie to the
                // edits (no observed change, failed re-read, coordinates, or a click
                // after the apply) makes the recorded state untrustworthy.
                ClearUndoJournal();
            }
        }
    }

    private async Task<ToolOutcome> ReadBackUndoStepAsync(
        ActiveUndo undo, ToolOutcome outcome, ControlObservation? before, RequestLease lease)
    {
        if (_pendingActions.Count > 0)
        {
            if (before is not null)
            {
                var between = await PollControlsAsync(before.Context, lease, _ => true);
                AdvancePendingControls(before, between);
            }

            return outcome;
        }

        _activeUndo = null;
        var after = await PollControlsAsync(undo.Context, lease,
            observation => ActionReadback.VerifyExpected(observation, undo.Plan.Expected, "observed").Success);
        if (after is null)
        {
            return outcome;
        }

        var restored = _undoJournal.VerifyUndo(after, undo.Plan);
        if (restored.Success)
        {
            _journalAwaitingApply = false;
        }

        AppendTranscript("Boo", restored.Message);
        return restored;
    }

    /// <summary>
    /// Re-reads the target's controls through the existing observation path, up to
    /// five times 120 ms apart, stopping as soon as <paramref name="settled"/> holds.
    /// Returns the last good observation, or null when the read failed or went stale.
    /// </summary>
    private async Task<ControlObservation?> PollControlsAsync(
        ScreenContext context, RequestLease lease, Func<ControlObservation, bool> settled)
    {
        const int attempts = 5;
        ControlObservation? last = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (!IsCurrent(lease.Generation) || !ReferenceEquals(_requestLease, lease))
            {
                return null;
            }

            if (attempt > 0)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(120), lease.Token);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }

                if (!IsCurrent(lease.Generation) || !ReferenceEquals(_requestLease, lease))
                {
                    return null;
                }
            }

            ControlObservation? observation;
            try
            {
                observation = await ReadRequestedControlsAsync(new CapturedFrame(context, Array.Empty<byte>()), lease);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
            {
                return null;
            }

            if (observation is null || observation.Controls.Count == 0 || !IsCurrent(lease.Generation))
            {
                return null;
            }

            last = observation;
            if (settled(observation))
            {
                break;
            }
        }

        return last;
    }

    private void AdvancePendingControls(ControlObservation before, ControlObservation? after)
    {
        if (!ReferenceEquals(_pendingControls, before))
        {
            return;
        }

        // Later steps compare against the newest observation. When the re-read failed,
        // drop the stale baseline so a later click cannot claim this step's change.
        _pendingControls = after;
    }

    private static bool SameWindow(WindowIdentity? left, WindowIdentity? right) =>
        left is not null && right is not null && left.Handle == right.Handle && left.ProcessId == right.ProcessId;

    private void ClearUndoJournal()
    {
        _undoJournal.Clear();
        _journalAwaitingApply = false;
    }

    private void ReportUndo(string message)
    {
        AppendTranscript("Boo", message);
        StatusText.Text = message;
    }

    private sealed record ActiveUndo(UndoPlan Plan, long Generation, ScreenContext Context);
}
