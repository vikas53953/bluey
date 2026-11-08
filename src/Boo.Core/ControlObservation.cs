using System.Collections.Immutable;

namespace Boo.Core;

public sealed record ControlCandidate(
    string AutomationId, string Name, string ControlType, string? Value,
    RectD Bounds, IReadOnlyList<int> RuntimeId, bool IsEnabled, bool IsOffscreen,
    bool IsPassword, bool CanInvoke, bool CanSetValue);

public sealed record ObservedControl(
    string AutomationId, string Name, string ControlType, string? Value,
    RectD Bounds, ImmutableArray<int> RuntimeId, bool CanInvoke, bool CanSetValue);

public sealed class ControlObservation
{
    private const int MaximumCandidates = 512;
    private const int MaximumControls = 128;
    private const int MaximumAutomationIdLength = 256;
    private const int MaximumNameLength = 512;
    private const int MaximumControlTypeLength = 128;
    private const int MaximumValueLength = 1024;
    private const int MaximumRuntimeIdLength = 64;

    private ControlObservation(ScreenContext context, IReadOnlyList<ObservedControl> controls)
    {
        Context = context;
        Controls = controls;
    }

    public ScreenContext Context { get; }
    public IReadOnlyList<ObservedControl> Controls { get; }

    public static ControlObservation Create(ScreenContext context, IEnumerable<ControlCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(candidates);
        ValidateContext(context);

        var eligible = new List<(ControlCandidate Candidate, ImmutableArray<int> RuntimeId)>();
        using var enumerator = candidates.GetEnumerator();
        var examined = 0;
        while (examined < MaximumCandidates && enumerator.MoveNext())
        {
            examined++;
            var candidate = enumerator.Current;
            if (!IsEligible(candidate, context, out var runtimeId))
            {
                continue;
            }

            eligible.Add((candidate, runtimeId));
        }

        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (candidate, _) in eligible)
        {
            occurrences.TryGetValue(candidate.AutomationId, out var count);
            occurrences[candidate.AutomationId] = count + 1;
        }

        var controls = ImmutableArray.CreateBuilder<ObservedControl>(
            Math.Min(MaximumControls, eligible.Count));
        foreach (var (candidate, runtimeId) in eligible)
        {
            if (occurrences[candidate.AutomationId] != 1)
            {
                continue;
            }

            controls.Add(new ObservedControl(
                candidate.AutomationId,
                candidate.Name,
                candidate.ControlType,
                candidate.Value is { Length: > MaximumValueLength } ? null : candidate.Value,
                candidate.Bounds,
                runtimeId,
                candidate.CanInvoke,
                candidate.CanSetValue));

            if (controls.Count == MaximumControls)
            {
                break;
            }
        }

        return new ControlObservation(context, controls.ToImmutable());
    }

    private static void ValidateContext(ScreenContext context)
    {
        var display = context.Display;
        var target = context.Target;
        if (display is null || target is null)
        {
            throw new ArgumentException("The observation context must include display and target identity.", nameof(context));
        }

        if (string.IsNullOrWhiteSpace(display.Id) ||
            !IsValidBounds(display.PixelBounds) ||
            !double.IsFinite(display.Scale) || display.Scale <= 0)
        {
            throw new ArgumentException("The display identity and geometry are invalid.", nameof(context));
        }

        if (target.Handle == 0 || target.ProcessId <= 0 ||
            string.IsNullOrWhiteSpace(target.ProcessName) ||
            !IsValidBounds(target.Bounds) ||
            context.FrameWidth <= 0 || context.FrameHeight <= 0)
        {
            throw new ArgumentException("The target identity, geometry, or frame dimensions are invalid.", nameof(context));
        }
    }

    private static bool IsEligible(
        ControlCandidate? candidate,
        ScreenContext context,
        out ImmutableArray<int> runtimeId)
    {
        runtimeId = default;
        if (candidate is null || !candidate.IsEnabled || candidate.IsOffscreen || candidate.IsPassword ||
            candidate.AutomationId is null || string.IsNullOrWhiteSpace(candidate.AutomationId) ||
            candidate.AutomationId.Length > MaximumAutomationIdLength ||
            candidate.Name is null || candidate.Name.Length > MaximumNameLength ||
            candidate.ControlType is null || string.IsNullOrWhiteSpace(candidate.ControlType) ||
            candidate.ControlType.Length > MaximumControlTypeLength ||
            candidate.RuntimeId is null ||
            candidate.RuntimeId.Count is < 1 or > MaximumRuntimeIdLength ||
            !IsContainedIn(candidate.Bounds, context.Display.PixelBounds) ||
            !IsContainedIn(candidate.Bounds, context.Target.Bounds))
        {
            return false;
        }

        runtimeId = ImmutableArray.CreateRange(candidate.RuntimeId);
        return true;
    }

    private static bool IsContainedIn(RectD bounds, RectD container)
    {
        if (!IsValidBounds(bounds) || !IsValidBounds(container))
        {
            return false;
        }

        var right = bounds.X + bounds.Width;
        var bottom = bounds.Y + bounds.Height;
        var containerRight = container.X + container.Width;
        var containerBottom = container.Y + container.Height;
        return bounds.X >= container.X && bounds.Y >= container.Y &&
            right <= containerRight && bottom <= containerBottom;
    }

    private static bool IsValidBounds(RectD bounds)
    {
        if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
            !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) ||
            bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        return double.IsFinite(bounds.X + bounds.Width) &&
            double.IsFinite(bounds.Y + bounds.Height);
    }
}
