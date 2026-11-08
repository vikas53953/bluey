namespace Boo.Core;

public sealed record UndoPlan(bool CanUndo, string? BlockedReason, IReadOnlyList<ToolRequest> Steps, IReadOnlyList<ExpectedControlState> Expected);

/// <summary>
/// Remembers the last verified edit transaction Boo made in one window (field
/// edits plus an optional observed apply) and plans a conservative reversal.
/// It never guesses: a newer change, an ambiguous Undo control, or a value it
/// cannot restore blocks the plan with a plain reason.
/// </summary>
public sealed class UndoJournal
{
    private const string NothingRecorded = "Nothing to undo: Boo has no recorded edit.";
    private const string DifferentWindow = "The edited window is no longer the current target.";
    private const string AmbiguousUndo = "More than one Undo control is visible; Boo will not guess.";

    private readonly List<EditRecord> _edits = new();
    private readonly List<ControlChange> _applyChanges = new();
    private string? _applyId;

    public bool HasPendingUndo => _edits.Count > 0 || _applyId is not null;

    public WindowIdentity? Window { get; private set; }

    public void RecordEdit(WindowIdentity window, string automationId, string? before, string after)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(automationId);
        ArgumentNullException.ThrowIfNull(after);

        if (Window is not null && !ControlDiff.SameWindow(Window, window))
        {
            Clear();
        }

        if (_applyId is not null)
        {
            Clear();
        }

        Window = window;
        var index = _edits.FindIndex(edit => string.Equals(edit.AutomationId, automationId, StringComparison.Ordinal));
        if (index >= 0)
        {
            _edits[index] = _edits[index] with { After = after };
        }
        else
        {
            _edits.Add(new EditRecord(automationId, before, after));
        }
    }

    public void RecordApply(WindowIdentity window, string automationId, IReadOnlyList<ControlChange> observedChanges)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(automationId);
        ArgumentNullException.ThrowIfNull(observedChanges);
        if (observedChanges.Count == 0)
        {
            return;
        }

        if (Window is not null && !ControlDiff.SameWindow(Window, window))
        {
            Clear();
        }

        Window = window;
        _applyId = automationId;
        foreach (var change in observedChanges)
        {
            ArgumentNullException.ThrowIfNull(change);
            var index = _applyChanges.FindIndex(existing =>
                string.Equals(existing.AutomationId, change.AutomationId, StringComparison.Ordinal) &&
                string.Equals(existing.Property, change.Property, StringComparison.Ordinal));
            if (index >= 0)
            {
                _applyChanges[index] = _applyChanges[index] with { After = change.After };
            }
            else
            {
                _applyChanges.Add(change);
            }
        }
    }

    public UndoPlan Plan(ControlObservation current, long generation)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!HasPendingUndo)
        {
            return Blocked(NothingRecorded);
        }

        var target = current.Context.Target;
        if (!ControlDiff.SameWindow(Window, target))
        {
            return Blocked(DifferentWindow);
        }

        foreach (var edit in _edits)
        {
            var control = ControlDiff.Find(current, edit.AutomationId);
            if (control is null || !string.Equals(control.Value, edit.After, StringComparison.Ordinal))
            {
                return Blocked(Conflict(edit.AutomationId));
            }
        }

        foreach (var change in _applyChanges)
        {
            var control = ControlDiff.Find(current, change.AutomationId);
            if (control is null ||
                !string.Equals(ControlDiff.Read(control, change.Property), change.After, StringComparison.Ordinal))
            {
                return Blocked(Conflict(change.AutomationId));
            }
        }

        ObservedControl? undoControl = null;
        if (_applyId is not null)
        {
            var undoControls = current.Controls
                .Where(control => control.CanInvoke &&
                    (control.AutomationId.Contains("undo", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(control.Name, "Undo", StringComparison.OrdinalIgnoreCase)))
                .Take(2)
                .ToList();
            if (undoControls.Count > 1)
            {
                return Blocked(AmbiguousUndo);
            }

            undoControl = undoControls.FirstOrDefault();
        }

        var processName = target.ProcessName;
        var steps = new List<ToolRequest>();
        if (undoControl is not null)
        {
            steps.Add(Request("click", processName, generation,
                ("automationId", undoControl.AutomationId), ("name", undoControl.Name)));
        }
        else
        {
            var empty = _edits.FirstOrDefault(edit => string.IsNullOrEmpty(edit.Before));
            if (empty is not null)
            {
                return Blocked($"{empty.AutomationId} was empty before the edit; Boo cannot restore an empty value.");
            }

            ObservedControl? applyControl = null;
            if (_applyId is not null)
            {
                applyControl = ControlDiff.Find(current, _applyId);
                if (applyControl is null)
                {
                    return Blocked($"{_applyId} is not visible; Boo cannot re-apply the original values.");
                }
            }

            for (var index = _edits.Count - 1; index >= 0; index--)
            {
                var edit = _edits[index];
                steps.Add(Request("type_text", processName, generation,
                    ("automationId", edit.AutomationId), ("text", edit.Before!)));
            }

            if (applyControl is not null)
            {
                steps.Add(Request("click", processName, generation,
                    ("automationId", applyControl.AutomationId), ("name", applyControl.Name)));
            }
        }

        var expected = new List<ExpectedControlState>();
        var listed = new HashSet<(string, string)>();
        foreach (var edit in _edits)
        {
            if (listed.Add((edit.AutomationId, ControlDiff.ValueProperty)))
            {
                expected.Add(new ExpectedControlState(edit.AutomationId, ControlDiff.ValueProperty, edit.Before));
            }
        }

        foreach (var change in _applyChanges)
        {
            if (listed.Add((change.AutomationId, change.Property)))
            {
                expected.Add(new ExpectedControlState(change.AutomationId, change.Property, change.Before));
            }
        }

        return new UndoPlan(true, null, steps.AsReadOnly(), expected.AsReadOnly());
    }

    public ToolOutcome VerifyUndo(ControlObservation after, UndoPlan plan)
    {
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanUndo)
        {
            throw new ArgumentException("Only an executable Undo plan can be verified.", nameof(plan));
        }

        var outcome = ActionReadback.VerifyExpected(after, plan.Expected, "observed Undo restored");
        if (outcome.Success)
        {
            Clear();
        }

        return outcome;
    }

    public void Clear()
    {
        _edits.Clear();
        _applyChanges.Clear();
        _applyId = null;
        Window = null;
    }

    private static string Conflict(string automationId) =>
        $"{automationId} changed since Boo edited it; Undo would overwrite a newer change.";

    private static UndoPlan Blocked(string reason) =>
        new(false, reason, Array.Empty<ToolRequest>(), Array.Empty<ExpectedControlState>());

    private static ToolRequest Request(string name, string target, long generation, params (string Key, string Value)[] arguments)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in arguments)
        {
            map[key] = value;
        }

        return new ToolRequest(name, target, map, generation);
    }

    private sealed record EditRecord(string AutomationId, string? Before, string After);
}
