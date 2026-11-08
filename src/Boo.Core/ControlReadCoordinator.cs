namespace Boo.Core;

public sealed class ControlReadCoordinator
{
    private readonly SemaphoreSlim _lifetimeGate = new(1, 1);

    public async Task<ControlObservation> ReadAsync(
        Func<Task<ControlObservation>> read,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);

        await _lifetimeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A cancellation race can still admit the waiter when the gate
            // becomes available.  Recheck before invoking native work.
            cancellationToken.ThrowIfCancellationRequested();

            var operation = read();
            var result = await operation.ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            _lifetimeGate.Release();
        }
    }
}
