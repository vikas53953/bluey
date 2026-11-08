using System.Windows;
using Boo.Core;

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
            // The capture path already reported why no frame was available.
            return;
        }

        var controls = await ReadRequestedControlsAsync(frame, lease);
        if (controls is null || !IsCurrent(lease.Generation) || !ReferenceEquals(_requestLease, lease))
        {
            return;
        }

        var plan = _undoJournal.Plan(controls, lease.Generation);
        if (!plan.CanUndo || plan.Steps.Count == 0)
        {
            ReportUndo(plan.BlockedReason ?? "Boo could not plan an Undo for the recorded edit.");
            return;
        }

        var authority = new TaskAuthority(lease.Generation, ExplicitUserTask: true, UndoTools, _settings.FileRoot);
        _activeUndo = new ActiveUndo(plan, lease.Generation);
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
    /// Runs after an approved action returned. Records verified edits, re-reads the
    /// target's controls after a click (and between queued steps), and replaces the
    /// click's unverified outcome with what the re-read actually observed.
    /// </summary>
    private async Task<ToolOutcome> ReadBackActionAsync(
        ToolRequest action, ToolOutcome outcome, ControlObservation? before, RequestLease lease)
    {
        if (!outcome.Committed || before is null || action.Name is not ("click" or "type_text"))
        {
            return outcome;
        }

        var undo = _activeUndo is { } active && active.Generation == action.Generation ? active.Plan : null;
        action.Arguments.TryGetValue("automationId", out var automationId);
        if (undo is null && action.Name == "type_text" && outcome.Success
            && !string.IsNullOrWhiteSpace(automationId)
            && action.Arguments.TryGetValue("text", out var typed) && typed is not null)
        {
            var field = before.Controls.FirstOrDefault(
                control => string.Equals(control.AutomationId, automationId, StringComparison.Ordinal));
            _undoJournal.RecordEdit(before.Context.Target, automationId, field?.Value, typed);
        }

        var lastUndoStep = undo is not null && _pendingActions.Count == 0;
        var needsReadBack = action.Name == "click" || lastUndoStep || _pendingActions.Count > 0;
        if (!needsReadBack || !IsCurrent(lease.Generation) || !ReferenceEquals(_requestLease, lease))
        {
            return outcome;
        }

        ControlObservation? after;
        try
        {
            after = await ReadRequestedControlsAsync(new CapturedFrame(before.Context, Array.Empty<byte>()), lease);
        }
        catch (OperationCanceledException)
        {
            return outcome;
        }

        if (after is null || after.Controls.Count == 0 || !IsCurrent(lease.Generation))
        {
            return outcome;
        }

        if (ReferenceEquals(_pendingControls, before))
        {
            // Later steps in this chain compare against the newest observation.
            _pendingControls = after;
        }

        if (undo is not null)
        {
            if (!lastUndoStep)
            {
                return outcome;
            }

            _activeUndo = null;
            var restored = _undoJournal.VerifyUndo(after, undo);
            AppendTranscript("Boo", restored.Message);
            return restored;
        }

        if (action.Name != "click")
        {
            return outcome;
        }

        var verified = ActionReadback.VerifyClick(before, after);
        if (verified.Success && !string.IsNullOrWhiteSpace(automationId))
        {
            _undoJournal.RecordApply(before.Context.Target, automationId, ControlDiff.Compare(before, after));
        }

        return verified;
    }

    private void ReportUndo(string message)
    {
        AppendTranscript("Boo", message);
        StatusText.Text = message;
    }

    private sealed record ActiveUndo(UndoPlan Plan, long Generation);
}
