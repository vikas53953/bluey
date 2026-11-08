using Boo.Core;

namespace Boo.Core.Tests;

public sealed class ControlReadCoordinatorTests
{
    private static readonly TimeSpan TestWait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Admits_one_callback_at_a_time_and_forwards_the_exact_result()
    {
        var coordinator = new ControlReadCoordinator();
        var firstEntered = NewSignal();
        var firstRelease = NewSignal();
        var firstSettled = NewSignal();
        var secondEntered = NewSignal();
        var secondRelease = NewSignal();
        var firstObservation = CreateObservation("first");
        var secondObservation = CreateObservation("second");
        var active = 0;
        var peak = 0;
        var secondSawFirstSettled = 0;
        Task<ControlObservation>? firstTask = null;
        Task<ControlObservation>? secondTask = null;

        try
        {
            firstTask = InvokeRead(coordinator, async () =>
            {
                ObserveEntry(ref active, ref peak);
                firstEntered.TrySetResult(null);
                try
                {
                    await firstRelease.Task;
                    firstSettled.TrySetResult(null);
                    return firstObservation;
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }, CancellationToken.None);
            await firstEntered.Task.WaitAsync(TestWait);

            secondTask = InvokeRead(coordinator, async () =>
            {
                ObserveEntry(ref active, ref peak);
                secondEntered.TrySetResult(null);
                if (firstSettled.Task.IsCompleted)
                    Interlocked.Exchange(ref secondSawFirstSettled, 1);

                try
                {
                    await secondRelease.Task;
                    return secondObservation;
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }, CancellationToken.None);

            Assert.False(secondEntered.Task.IsCompleted,
                "A second callback must remain queued while the first callback is unfinished.");

            firstRelease.TrySetResult(null);
            var firstResult = await firstTask.WaitAsync(TestWait);
            await secondEntered.Task.WaitAsync(TestWait);

            secondRelease.TrySetResult(null);
            var secondResult = await secondTask.WaitAsync(TestWait);

            Assert.Same(firstObservation, firstResult);
            Assert.Same(secondObservation, secondResult);
            Assert.Equal(1, peak);
            Assert.Equal(1, Volatile.Read(ref secondSawFirstSettled));
        }
        finally
        {
            firstRelease.TrySetResult(null);
            secondRelease.TrySetResult(null);
            await ObserveSafelyAsync(firstTask);
            await ObserveSafelyAsync(secondTask);
        }
    }

    [Fact]
    public async Task A_pre_cancelled_caller_never_invokes_the_callback()
    {
        var coordinator = new ControlReadCoordinator();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var invocationCount = 0;

        var operation = InvokeRead(coordinator, () =>
        {
            Interlocked.Increment(ref invocationCount);
            return Task.FromResult(CreateObservation("should-not-run"));
        }, cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TestWait));
        Assert.Equal(0, Volatile.Read(ref invocationCount));
    }

    [Fact]
    public async Task A_queued_cancellation_never_invokes_the_callback_and_leaves_the_coordinator_usable()
    {
        var coordinator = new ControlReadCoordinator();
        var firstEntered = NewSignal();
        var firstRelease = NewSignal();
        var canceledRelease = NewSignal();
        var invocationCount = 0;
        Task<ControlObservation>? firstTask = null;
        Task<ControlObservation>? canceledTask = null;

        using var cancellation = new CancellationTokenSource();
        try
        {
            firstTask = InvokeRead(coordinator, async () =>
            {
                Interlocked.Increment(ref invocationCount);
                firstEntered.TrySetResult(null);
                await firstRelease.Task;
                return CreateObservation("first");
            }, CancellationToken.None);
            await firstEntered.Task.WaitAsync(TestWait);

            var canceledEntered = NewSignal();
            canceledTask = InvokeRead(coordinator, async () =>
            {
                Interlocked.Increment(ref invocationCount);
                canceledEntered.TrySetResult(null);
                await canceledRelease.Task;
                return CreateObservation("canceled");
            }, cancellation.Token);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledTask.WaitAsync(TestWait));

            Assert.Equal(1, Volatile.Read(ref invocationCount));
            Assert.False(canceledEntered.Task.IsCompleted,
                "A canceled waiter must not start its callback.");

            firstRelease.TrySetResult(null);
            await firstTask.WaitAsync(TestWait);

            var next = await InvokeRead(
                coordinator,
                () =>
                {
                    Interlocked.Increment(ref invocationCount);
                    return Task.FromResult(CreateObservation("next"));
                },
                CancellationToken.None).WaitAsync(TestWait);

            Assert.Equal("next", next.Controls[0].AutomationId);
            Assert.Equal(2, Volatile.Read(ref invocationCount));
        }
        finally
        {
            firstRelease.TrySetResult(null);
            canceledRelease.TrySetResult(null);
            await ObserveSafelyAsync(firstTask);
            await ObserveSafelyAsync(canceledTask);
        }
    }

    [Fact]
    public async Task In_flight_cancellation_holds_admission_and_discards_a_late_success()
    {
        var coordinator = new ControlReadCoordinator();
        var firstEntered = NewSignal();
        var firstRelease = NewSignal();
        var firstSettled = NewSignal();
        var secondEntered = NewSignal();
        var secondRelease = NewSignal();
        var overlap = 0;
        Task<ControlObservation>? firstTask = null;
        Task<ControlObservation>? secondTask = null;

        using var cancellation = new CancellationTokenSource();
        try
        {
            firstTask = InvokeRead(coordinator, async () =>
            {
                firstEntered.TrySetResult(null);
                try
                {
                    await firstRelease.Task;
                    return CreateObservation("late-success");
                }
                finally
                {
                    firstSettled.TrySetResult(null);
                }
            }, cancellation.Token);
            await firstEntered.Task.WaitAsync(TestWait);

            secondTask = InvokeRead(coordinator, async () =>
            {
                if (!firstSettled.Task.IsCompleted)
                    Interlocked.Exchange(ref overlap, 1);

                secondEntered.TrySetResult(null);
                await secondRelease.Task;
                return CreateObservation("next");
            }, CancellationToken.None);

            cancellation.Cancel();
            Assert.False(firstTask.IsCompleted,
                "An in-flight cancellation must not complete while its callback task is still held.");
            Assert.False(secondEntered.Task.IsCompleted,
                "The next callback must remain queued while the canceled callback task is held.");
            firstRelease.TrySetResult(null);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstTask.WaitAsync(TestWait));
            await secondEntered.Task.WaitAsync(TestWait);
            secondRelease.TrySetResult(null);

            var secondResult = await secondTask.WaitAsync(TestWait);
            Assert.Equal("next", secondResult.Controls[0].AutomationId);
            Assert.Equal(0, Volatile.Read(ref overlap));
        }
        finally
        {
            firstRelease.TrySetResult(null);
            secondRelease.TrySetResult(null);
            await ObserveSafelyAsync(firstTask);
            await ObserveSafelyAsync(secondTask);
        }
    }

    [Fact]
    public async Task A_faulted_callback_releases_admission_for_the_next_read()
    {
        var coordinator = new ControlReadCoordinator();
        var expected = new InvalidOperationException("callback failed");
        var firstEntered = NewSignal();
        var firstRelease = NewSignal();
        var secondEntered = NewSignal();
        Task<ControlObservation>? first = null;
        Task<ControlObservation>? second = null;

        try
        {
            first = InvokeRead(coordinator, async () =>
            {
                firstEntered.TrySetResult(null);
                await firstRelease.Task;
                throw expected;
            }, CancellationToken.None);
            await firstEntered.Task.WaitAsync(TestWait);

            second = InvokeRead(coordinator, () =>
            {
                secondEntered.TrySetResult(null);
                return Task.FromResult(CreateObservation("next"));
            }, CancellationToken.None);

            Assert.False(secondEntered.Task.IsCompleted,
                "A faulted callback must remain queued behind the active callback.");

            firstRelease.TrySetResult(null);
            await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(TestWait));
            await secondEntered.Task.WaitAsync(TestWait);
            var next = await second.WaitAsync(TestWait);

            Assert.Equal("next", next.Controls[0].AutomationId);
        }
        finally
        {
            firstRelease.TrySetResult(null);
            await ObserveSafelyAsync(first);
            await ObserveSafelyAsync(second);
        }
    }

    [Fact]
    public async Task A_synchronously_throwing_callback_releases_admission_for_the_next_read()
    {
        var coordinator = new ControlReadCoordinator();
        var expected = new InvalidOperationException("synchronous callback failure");

        var first = InvokeRead(coordinator, () => throw expected, CancellationToken.None);
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(TestWait));

        var next = await InvokeRead(
            coordinator,
            () => Task.FromResult(CreateObservation("next")),
            CancellationToken.None).WaitAsync(TestWait);

        Assert.Same(expected, actual);
        Assert.Equal("next", next.Controls[0].AutomationId);
    }

    [Fact]
    public async Task A_canceled_callback_releases_admission_for_the_next_read()
    {
        var coordinator = new ControlReadCoordinator();
        using var callbackCancellation = new CancellationTokenSource();
        callbackCancellation.Cancel();
        var firstEntered = NewSignal();
        var firstRelease = NewSignal();
        var secondEntered = NewSignal();
        Task<ControlObservation>? first = null;
        Task<ControlObservation>? second = null;

        try
        {
            first = InvokeRead(coordinator, async () =>
            {
                firstEntered.TrySetResult(null);
                await firstRelease.Task;
                return CreateObservation("first");
            }, CancellationToken.None);
            await firstEntered.Task.WaitAsync(TestWait);

            second = InvokeRead(coordinator, () =>
            {
                secondEntered.TrySetResult(null);
                return Task.FromCanceled<ControlObservation>(callbackCancellation.Token);
            }, CancellationToken.None);

            Assert.False(secondEntered.Task.IsCompleted,
                "A canceled callback must remain queued behind the active callback.");

            firstRelease.TrySetResult(null);
            await first.WaitAsync(TestWait);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TestWait));

            var next = await InvokeRead(
                coordinator,
                () => Task.FromResult(CreateObservation("next")),
                CancellationToken.None).WaitAsync(TestWait);

            Assert.Equal("next", next.Controls[0].AutomationId);
        }
        finally
        {
            firstRelease.TrySetResult(null);
            await ObserveSafelyAsync(first);
            await ObserveSafelyAsync(second);
        }
    }

    [Fact]
    public async Task A_null_callback_is_rejected_with_ArgumentNullException()
    {
        var coordinator = new ControlReadCoordinator();

        var operation = InvokeRead(coordinator, null!, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentNullException>(() => operation.WaitAsync(TestWait));
    }

    private static TaskCompletionSource<object?> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task<ControlObservation> InvokeRead(
        ControlReadCoordinator coordinator,
        Func<Task<ControlObservation>> read,
        CancellationToken cancellationToken)
    {
        try
        {
            return coordinator.ReadAsync(read, cancellationToken);
        }
        catch (Exception error)
        {
            return Task.FromException<ControlObservation>(error);
        }
    }

    private static void ObserveEntry(ref int active, ref int peak)
    {
        var current = Interlocked.Increment(ref active);
        while (true)
        {
            var observedPeak = Volatile.Read(ref peak);
            if (current <= observedPeak ||
                Interlocked.CompareExchange(ref peak, current, observedPeak) == observedPeak)
            {
                return;
            }
        }
    }

    private static async Task ObserveSafelyAsync(Task? task)
    {
        if (task is null)
            return;

        try
        {
            await task.WaitAsync(TestWait);
        }
        catch
        {
            // Preserve the primary assertion while keeping every controlled
            // callback bounded during failure cleanup.
        }
    }

    private static ControlObservation CreateObservation(string automationId) =>
        ControlObservation.Create(
            new ScreenContext(
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Generation: 17,
                new DisplayInfo("display", "Fixture", new RectD(0, 0, 1920, 1080), 1),
                FrameWidth: 1920,
                FrameHeight: 1080,
                new WindowIdentity(42, 7, "fixture", new RectD(20, 30, 800, 600)),
                DateTimeOffset.UnixEpoch),
            new[]
            {
                new ControlCandidate(
                    automationId,
                    "Fixture control",
                    "Button",
                    "value",
                    new RectD(40, 50, 100, 40),
                    new[] { 1, 2, 3 },
                    IsEnabled: true,
                    IsOffscreen: false,
                    IsPassword: false,
                    CanInvoke: true,
                    CanSetValue: false)
            });
}
