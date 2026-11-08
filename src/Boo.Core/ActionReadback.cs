namespace Boo.Core;

public sealed record ControlChange(string AutomationId, string Property, string? Before, string? After);

public sealed record ExpectedControlState(string AutomationId, string Property, string? Value);

/// <summary>Compares two observations of the same window, control by control.</summary>
public static class ControlDiff
{
    internal const string NameProperty = "Name";
    internal const string ValueProperty = "Value";

    public static IReadOnlyList<ControlChange> Compare(ControlObservation before, ControlObservation after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (!SameWindow(before.Context.Target, after.Context.Target))
        {
            throw new ArgumentException("The observations belong to different windows.", nameof(after));
        }

        var afterControls = Index(after);
        var changes = new List<ControlChange>();
        foreach (var control in before.Controls)
        {
            if (!afterControls.TryGetValue(control.AutomationId, out var current))
            {
                continue;
            }

            if (!string.Equals(control.Name, current.Name, StringComparison.Ordinal))
            {
                changes.Add(new ControlChange(control.AutomationId, NameProperty, control.Name, current.Name));
            }

            if (!string.Equals(control.Value, current.Value, StringComparison.Ordinal))
            {
                changes.Add(new ControlChange(control.AutomationId, ValueProperty, control.Value, current.Value));
            }
        }

        return changes.AsReadOnly();
    }

    internal static bool SameWindow(WindowIdentity? left, WindowIdentity? right) =>
        left is not null && right is not null &&
        left.Handle == right.Handle && left.ProcessId == right.ProcessId;

    internal static Dictionary<string, ObservedControl> Index(ControlObservation observation)
    {
        var index = new Dictionary<string, ObservedControl>(StringComparer.Ordinal);
        foreach (var control in observation.Controls)
        {
            index.TryAdd(control.AutomationId, control);
        }

        return index;
    }

    internal static ObservedControl? Find(ControlObservation observation, string automationId) =>
        observation.Controls.FirstOrDefault(
            control => string.Equals(control.AutomationId, automationId, StringComparison.Ordinal));

    internal static string? Read(ObservedControl control, string property) =>
        property == NameProperty ? control.Name : control.Value;
}

/// <summary>
/// Reports what an action visibly did, using only re-read control state.
/// Nothing is claimed as verified unless an observation shows it.
/// </summary>
public static class ActionReadback
{
    public static ToolOutcome VerifyClick(ControlObservation before, ControlObservation after)
    {
        var changes = ControlDiff.Compare(before, after);
        return changes.Count > 0
            ? new ToolOutcome(true, true, "observed after click: " + string.Join("; ", changes.Select(Describe)))
            : new ToolOutcome(false, true, "partial/unverified: the click was issued but no observed control changed.");
    }

    public static ToolOutcome VerifyExpected(ControlObservation after, IEnumerable<ExpectedControlState> expected, string successPrefix)
    {
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(expected);
        var states = expected.ToList();
        if (states.Count == 0)
        {
            throw new ArgumentException("At least one expected control state is required.", nameof(expected));
        }

        foreach (var state in states)
        {
            ArgumentNullException.ThrowIfNull(state);
            var control = ControlDiff.Find(after, state.AutomationId);
            if (control is null)
            {
                return new ToolOutcome(false, true, $"partial/unverified: {state.AutomationId} is not visible");
            }

            var actual = ControlDiff.Read(control, state.Property);
            if (!string.Equals(actual, state.Value, StringComparison.Ordinal))
            {
                return new ToolOutcome(false, true,
                    $"partial/unverified: {state.AutomationId}.{state.Property} is {Render(actual)} but expected {Render(state.Value)}");
            }
        }

        return new ToolOutcome(true, true, successPrefix + ": " + string.Join("; ",
            states.Select(state => $"{state.AutomationId}.{state.Property} = {Render(state.Value)}")));
    }

    public static string Describe(ControlChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return $"{change.AutomationId}.{change.Property}: {Render(change.Before)} -> {Render(change.After)}";
    }

    private static string Render(string? value) => value is null ? "(none)" : $"\"{value}\"";
}
