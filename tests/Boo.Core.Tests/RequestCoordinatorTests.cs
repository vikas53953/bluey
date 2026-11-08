using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Boo.Core;
using Xunit;

namespace Boo.Core.Tests;

public sealed class RequestCoordinatorTests
{
    // RC-001
    [Fact]
    public void Begin_invalidates_the_prior_lease_and_cancels_its_token()
    {
        using var coordinator = new RequestCoordinator();

        var first = coordinator.Begin();
        var second = coordinator.Begin();

        Assert.NotEqual(first.Generation, second.Generation);
        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(coordinator.IsCurrent(first.Generation));
        Assert.True(coordinator.IsCurrent(second.Generation));
        Assert.False(second.Token.IsCancellationRequested);
    }

    // RC-002
    [Fact]
    public void Stop_rejects_a_late_generation_and_does_not_run_its_effect()
    {
        var coordinator = new RequestCoordinator();
        var lease = coordinator.Begin();
        var effects = 0;

        coordinator.Stop();

        Assert.True(lease.Token.IsCancellationRequested);
        Assert.False(coordinator.IsCurrent(lease.Generation));
        Assert.False(coordinator.TryCommit(lease.Generation, () => effects++));
        Assert.Equal(0, effects);

        coordinator.Dispose();
    }

    // RC-003
    [Fact]
    public void A_current_generation_can_commit_its_effect_before_stop()
    {
        var coordinator = new RequestCoordinator();
        var lease = coordinator.Begin();
        var effects = 0;

        Assert.True(coordinator.TryCommit(lease.Generation, () => effects++));
        Assert.Equal(1, effects);

        coordinator.Stop();
        Assert.False(coordinator.IsCurrent(lease.Generation));
        coordinator.Dispose();
    }

    // RC-004
    [Fact]
    public async Task Stop_and_commit_share_a_serialized_boundary_when_forced_to_overlap()
    {
        var coordinator = new RequestCoordinator();
        var lease = coordinator.Begin();
        using var effectEntered = new ManualResetEventSlim();
        using var releaseEffect = new ManualResetEventSlim();
        using var stopStarted = new ManualResetEventSlim();
        var order = new ConcurrentQueue<string>();

        var commit = Task.Run(() => coordinator.TryCommit(lease.Generation, () =>
        {
            effectEntered.Set();
            releaseEffect.Wait();
            order.Enqueue("effect");
        }));

        Task? stop = null;

        try
        {
            await WaitForSignalAsync(effectEntered, "TryCommit did not enter its controlled effect.");

            // Start Stop only after the commit has entered its controlled
            // effect. This removes an accidental Stop-before-commit ordering
            // from the fixture while still forcing both calls to overlap.
            stop = Task.Run(() =>
            {
                stopStarted.Set();
                coordinator.Stop();
                order.Enqueue("stop");
            });
            await WaitForSignalAsync(stopStarted, "Stop did not reach the controlled overlap.");

            // Release the commit only after Stop has been requested. A correct
            // serialized boundary lets the already-entered commit finish first.
            releaseEffect.Set();
            await Task.WhenAll(commit, stop).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseEffect.Set();
            if (stop is null)
            {
                await commit.WaitAsync(TimeSpan.FromSeconds(5));
            }
            else
            {
                await Task.WhenAll(commit, stop).WaitAsync(TimeSpan.FromSeconds(5));
            }
            coordinator.Dispose();
        }

        Assert.True(await commit);
        Assert.Equal(new[] { "effect", "stop" }, order.ToArray());
        Assert.False(coordinator.IsCurrent(lease.Generation));
    }

    private static async Task WaitForSignalAsync(ManualResetEventSlim signal, string message)
    {
        var signaled = await Task.Run(() => signal.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(signaled, message);
    }

    // RC-005
    [Fact]
    public void Dispose_stops_the_current_generation_and_disallows_begin()
    {
        var coordinator = new RequestCoordinator();
        var lease = coordinator.Begin();

        coordinator.Dispose();

        Assert.True(lease.Token.IsCancellationRequested);
        Assert.False(coordinator.IsCurrent(lease.Generation));
        Assert.False(coordinator.TryCommit(lease.Generation, () => { }));
        Assert.Throws<ObjectDisposedException>(() => coordinator.Begin());
    }
}
