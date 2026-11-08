using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using Boo.Core;

namespace Boo.Windows;

/// <summary>
/// Reads a bounded, copied UI Automation snapshot for the target retained by
/// <see cref="TargetObserver"/>.  The UI Automation objects never leave the
/// worker thread.
/// </summary>
public sealed class ControlObservationReader
{
    private const int MaximumVisitedElements = 512;
    private const string UnavailableMessage =
        "Windows could not observe UI Automation controls for the selected target.";
    private static readonly ControlReadCoordinator ReadCoordinator = new();

    private readonly TargetObserver _targets;

    public ControlObservationReader(TargetObserver targets)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
    }

    public async Task<ControlObservation> ReadAsync(ScreenContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        return await ReadCoordinator.ReadAsync(
            () => StartWorker(context, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private Task<ControlObservation> StartWorker(
        ScreenContext context,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<ControlObservation>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var worker = new Thread(() => RunWorker(context, cancellationToken, completion))
        {
            IsBackground = true,
            Name = "Boo UI Automation observation"
        };

        // UI Automation clients must use a windowless COM MTA thread.  This
        // worker creates no windows and every AutomationElement is consumed
        // before the worker exits.
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        return completion.Task;
    }

    private void RunWorker(
        ScreenContext context,
        CancellationToken cancellationToken,
        TaskCompletionSource<ControlObservation> completion)
    {
        try
        {
            var observation = ReadOnWorker(context, cancellationToken);
            completion.TrySetResult(observation);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            else
            {
                completion.TrySetException(CreateUnavailable());
            }
        }
        catch (CaptureLimitationException error)
        {
            completion.TrySetException(error);
        }
        catch (ArgumentException error)
        {
            completion.TrySetException(error);
        }
        catch (Exception)
        {
            // Provider text and native exception details are intentionally not
            // exposed to WPF or included in the user-visible limitation.
            completion.TrySetException(CreateUnavailable());
        }
    }

    private ControlObservation ReadOnWorker(ScreenContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_targets.IsCurrent(context.Target))
        {
            throw CreateUnavailable();
        }

        var cacheRequest = CreateCacheRequest();
        AutomationElement root;

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            root = AutomationElement.FromHandle(new nint(context.Target.Handle));
        }
        catch (ElementNotAvailableException)
        {
            throw CreateUnavailable();
        }
        catch (Exception error) when (IsNativeFailure(error))
        {
            throw CreateUnavailable();
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            root = root.GetUpdatedCache(cacheRequest);
        }
        catch (ElementNotAvailableException)
        {
            throw CreateUnavailable();
        }
        catch (Exception error) when (IsNativeFailure(error))
        {
            throw CreateUnavailable();
        }

        TreeWalker walker;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            walker = TreeWalker.ControlViewWalker;
        }
        catch (Exception error) when (IsNativeFailure(error))
        {
            throw CreateUnavailable();
        }

        var candidates = Walk(root, walker, cacheRequest, context, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        if (!_targets.IsCurrent(context.Target))
        {
            throw CreateUnavailable();
        }

        return ControlObservation.Create(context, candidates);
    }

    private static CacheRequest CreateCacheRequest()
    {
        var request = new CacheRequest
        {
            AutomationElementMode = AutomationElementMode.Full,
            TreeFilter = Automation.ControlViewCondition,
            TreeScope = TreeScope.Element
        };

        request.Add(AutomationElement.AutomationIdProperty);
        request.Add(AutomationElement.NameProperty);
        request.Add(AutomationElement.ControlTypeProperty);
        request.Add(AutomationElement.BoundingRectangleProperty);
        request.Add(AutomationElement.IsEnabledProperty);
        request.Add(AutomationElement.IsOffscreenProperty);
        request.Add(AutomationElement.IsPasswordProperty);
        request.Add(AutomationElement.RuntimeIdProperty);
        return request;
    }

    private static List<ControlCandidate> Walk(
        AutomationElement root,
        TreeWalker walker,
        CacheRequest cacheRequest,
        ScreenContext context,
        CancellationToken cancellationToken)
    {
        var candidates = new List<ControlCandidate>();
        var work = new Stack<TraversalStep>();
        var visited = 1;
        work.Push(TraversalStep.Process(root, isRoot: true));

        while (work.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = work.Pop();

            if (step.GetSibling)
            {
                if (visited >= MaximumVisitedElements)
                {
                    continue;
                }

                AutomationElement? sibling;
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    sibling = walker.GetNextSibling(step.Element, cacheRequest);
                }
                catch (ElementNotAvailableException)
                {
                    continue;
                }
                catch (Exception error) when (IsNativeFailure(error))
                {
                    throw CreateUnavailable();
                }

                if (sibling is null)
                {
                    continue;
                }

                visited++;
                work.Push(TraversalStep.Sibling(sibling));
                work.Push(TraversalStep.Process(sibling, isRoot: false));
                continue;
            }

            var candidate = ReadCandidate(step.Element, context, cancellationToken, step.IsRoot);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }

            if (visited >= MaximumVisitedElements)
            {
                continue;
            }

            AutomationElement? child;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                child = walker.GetFirstChild(step.Element, cacheRequest);
            }
            catch (ElementNotAvailableException)
            {
                // A child whose provider disappeared is not evidence that the
                // retained target itself disappeared.  Omit this subtree.
                if (step.IsRoot)
                {
                    throw CreateUnavailable();
                }

                continue;
            }
            catch (Exception error) when (IsNativeFailure(error))
            {
                throw CreateUnavailable();
            }

            if (child is null)
            {
                continue;
            }

            visited++;
            work.Push(TraversalStep.Sibling(child));
            work.Push(TraversalStep.Process(child, isRoot: false));
        }

        return candidates;
    }

    private static ControlCandidate? ReadCandidate(
        AutomationElement element,
        ScreenContext context,
        CancellationToken cancellationToken,
        bool isRoot)
    {
        try
        {
            var automationId = CachedString(element, AutomationElement.AutomationIdProperty);
            if (string.IsNullOrWhiteSpace(automationId) || automationId.Length > 256)
            {
                return null;
            }

            var name = CachedString(element, AutomationElement.NameProperty);
            if (name is null || name.Length > 512)
            {
                return null;
            }

            var controlType = CachedValue(element, AutomationElement.ControlTypeProperty) as ControlType;
            var controlTypeName = controlType?.ProgrammaticName;
            if (string.IsNullOrWhiteSpace(controlTypeName) || controlTypeName.Length > 128)
            {
                return null;
            }

            var enabled = CachedBoolean(element, AutomationElement.IsEnabledProperty);
            var offscreen = CachedBoolean(element, AutomationElement.IsOffscreenProperty);
            var password = CachedBoolean(element, AutomationElement.IsPasswordProperty);
            if (enabled is not true || offscreen is not false || password is not false)
            {
                return null;
            }

            var bounds = CachedBounds(element);
            if (bounds is not { } candidateBounds ||
                !IsEligibleBounds(candidateBounds, context.Display.PixelBounds, context.Target.Bounds))
            {
                return null;
            }

            var runtimeId = CachedRuntimeId(element);
            if (runtimeId is null)
            {
                return null;
            }

            // Do not request or read ValuePattern.Value until password and
            // visibility have been checked above.
            cancellationToken.ThrowIfCancellationRequested();
            var canInvoke = element.TryGetCurrentPattern(InvokePattern.Pattern, out var invokePattern) &&
                invokePattern is InvokePattern;

            string? value = null;
            var canSetValue = false;
            cancellationToken.ThrowIfCancellationRequested();
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObject) &&
                valuePatternObject is ValuePattern valuePattern)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isReadOnly = valuePattern.Current.IsReadOnly;
                cancellationToken.ThrowIfCancellationRequested();
                value = valuePattern.Current.Value;
                canSetValue = !isReadOnly;
            }

            return new ControlCandidate(
                automationId,
                name,
                controlTypeName,
                value,
                candidateBounds,
                runtimeId,
                enabled.Value,
                offscreen.Value,
                password.Value,
                canInvoke,
                canSetValue);
        }
        catch (ElementNotAvailableException) when (!isRoot)
        {
            // Providers can remove a single element during a bounded walk.
            return null;
        }
        catch (ElementNotAvailableException)
        {
            throw CreateUnavailable();
        }
        catch (Exception error) when (IsNativeFailure(error))
        {
            throw CreateUnavailable();
        }
    }

    private static string? CachedString(AutomationElement element, AutomationProperty property) =>
        CachedValue(element, property) as string;

    private static bool? CachedBoolean(AutomationElement element, AutomationProperty property) =>
        CachedValue(element, property) is bool value ? value : null;

    private static RectD? CachedBounds(AutomationElement element)
    {
        if (CachedValue(element, AutomationElement.BoundingRectangleProperty) is not Rect bounds)
        {
            return null;
        }

        return new RectD(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private static int[]? CachedRuntimeId(AutomationElement element)
    {
        if (CachedValue(element, AutomationElement.RuntimeIdProperty) is not int[] runtimeId ||
            runtimeId.Length is < 1 or > 64)
        {
            return null;
        }

        return runtimeId.ToArray();
    }

    private static object? CachedValue(AutomationElement element, AutomationProperty property)
    {
        var value = element.GetCachedPropertyValue(property, true);
        return ReferenceEquals(value, AutomationElement.NotSupported) ? null : value;
    }

    private static bool IsEligibleBounds(RectD bounds, RectD display, RectD target) =>
        IsFinitePositive(bounds) && Contains(display, bounds) && Contains(target, bounds);

    private static bool IsFinitePositive(RectD rect) =>
        double.IsFinite(rect.X) && double.IsFinite(rect.Y) &&
        double.IsFinite(rect.Width) && double.IsFinite(rect.Height) &&
        rect.Width > 0 && rect.Height > 0 &&
        double.IsFinite(rect.X + rect.Width) && double.IsFinite(rect.Y + rect.Height);

    private static bool Contains(RectD outer, RectD inner) =>
        IsFinitePositive(outer) &&
        inner.X >= outer.X && inner.Y >= outer.Y &&
        inner.X + inner.Width <= outer.X + outer.Width &&
        inner.Y + inner.Height <= outer.Y + outer.Height;

    private static bool IsNativeFailure(Exception error) =>
        error is COMException or InvalidOperationException or ArgumentException or
        UnauthorizedAccessException or Win32Exception;

    private static CaptureLimitationException CreateUnavailable() =>
        new(UnavailableMessage);

    private readonly record struct TraversalStep(
        AutomationElement Element,
        bool GetSibling,
        bool IsRoot)
    {
        public static TraversalStep Process(AutomationElement element, bool isRoot) =>
            new(element, GetSibling: false, isRoot);

        public static TraversalStep Sibling(AutomationElement element) =>
            new(element, GetSibling: true, IsRoot: false);
    }
}
