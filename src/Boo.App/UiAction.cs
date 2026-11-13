using System.Runtime.ExceptionServices;
using System.Threading;

namespace Boo.App;

/// <summary>
/// Starts async work from a synchronous handler (a WPF click, a realtime event) without <c>async void</c>
/// (BOO-ASYNC-003). The handler stays a one-liner: <c>UiAction.Run(SendAsync)</c>.
/// A failure the work does not handle itself goes exactly where an <c>async void</c> handler's failure went:
/// it is re-thrown on the synchronization context that was current when the work started (on the WPF UI thread
/// that is App's DispatcherUnhandledException crash path, with its recover-or-stop decision), or on the thread
/// pool when there was none. Nothing is caught and kept quiet here, so behaviour is unchanged.
/// </summary>
internal static class UiAction
{
    /// <summary>Starts <paramref name="work"/> now, on the calling thread, and routes an unhandled failure as described above.</summary>
    public static void Run(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var context = SynchronizationContext.Current;
        _ = ObserveAsync(work(), context);
    }

    private static async Task ObserveAsync(Task task, SynchronizationContext? context)
    {
        await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (task.IsCompletedSuccessfully)
        {
            return;
        }

        Exception failure;
        if (task.Exception is { } faulted)
        {
            failure = faulted.InnerExceptions.Count == 1 ? faulted.InnerExceptions[0] : faulted;
        }
        else
        {
            failure = await CancellationOf(task);
        }

        var rethrow = ExceptionDispatchInfo.Capture(failure);
        if (context is not null)
        {
            context.Post(static state => ((ExceptionDispatchInfo)state!).Throw(), rethrow);
        }
        else
        {
            ThreadPool.QueueUserWorkItem(static state => ((ExceptionDispatchInfo)state!).Throw(), rethrow);
        }
    }

    /// <summary>The original <see cref="OperationCanceledException"/> a cancelled task stored (awaiting it re-throws that object).</summary>
    private static async Task<Exception> CancellationOf(Task cancelled)
    {
        try
        {
            await cancelled.ConfigureAwait(false);
        }
        catch (OperationCanceledException original)
        {
            return original;
        }
        return new TaskCanceledException(cancelled);
    }
}
