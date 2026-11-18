// Banned-API canary, base class library part (docs/quality/RULES.md#canary). Each call line ends in a BAN marker
// comment naming its doc id; it must bind to exactly that line of src/BannedSymbols.txt and fire RS0030 exactly once.
// scripts/quality/banned_apis.py builds this file in Canary.csproj and also inside every src/ project (the probe),
// so everything is fully qualified and nothing here may depend on a project's usings.
#pragma warning disable CA1416, CA2012, CA1822, CA1849, CA2016, CS0618
namespace BooBannedApiCanary.Core;

internal static class Calls
{
    internal static void All(
        global::System.Threading.Tasks.Task t,
        global::System.Threading.Tasks.Task<int> ti,
        global::System.Threading.Tasks.ValueTask v,
        global::System.Threading.Tasks.ValueTask<int> vi,
        global::System.Threading.Tasks.Task[] tasks,
        global::System.ReadOnlySpan<global::System.Threading.Tasks.Task> span,
        global::System.Collections.Generic.IEnumerable<global::System.Threading.Tasks.Task> sequence,
        global::System.Threading.CancellationToken ct,
        global::System.TimeSpan ts,
        global::System.Diagnostics.Process process,
        global::System.Diagnostics.ProcessStartInfo info,
        global::System.Collections.Generic.IEnumerable<string> arguments,
        global::System.Security.SecureString secret)
    {
        global::System.Threading.Thread.Sleep(1); // BAN M:System.Threading.Thread.Sleep(System.Int32)
        global::System.Threading.Thread.Sleep(ts); // BAN M:System.Threading.Thread.Sleep(System.TimeSpan)

        t.Wait(); // BAN M:System.Threading.Tasks.Task.Wait
        _ = t.Wait(1); // BAN M:System.Threading.Tasks.Task.Wait(System.Int32)
        _ = t.Wait(1, ct); // BAN M:System.Threading.Tasks.Task.Wait(System.Int32,System.Threading.CancellationToken)
        t.Wait(ct); // BAN M:System.Threading.Tasks.Task.Wait(System.Threading.CancellationToken)
        _ = t.Wait(ts); // BAN M:System.Threading.Tasks.Task.Wait(System.TimeSpan)
        _ = t.Wait(ts, ct); // BAN M:System.Threading.Tasks.Task.Wait(System.TimeSpan,System.Threading.CancellationToken)
        global::System.Threading.Tasks.Task.WaitAll(sequence, ct); // BAN M:System.Threading.Tasks.Task.WaitAll(System.Collections.Generic.IEnumerable{System.Threading.Tasks.Task},System.Threading.CancellationToken)
        global::System.Threading.Tasks.Task.WaitAll(span); // BAN M:System.Threading.Tasks.Task.WaitAll(System.ReadOnlySpan{System.Threading.Tasks.Task})
        global::System.Threading.Tasks.Task.WaitAll(tasks); // BAN M:System.Threading.Tasks.Task.WaitAll(System.Threading.Tasks.Task[])
        _ = global::System.Threading.Tasks.Task.WaitAll(tasks, 1); // BAN M:System.Threading.Tasks.Task.WaitAll(System.Threading.Tasks.Task[],System.Int32)
        _ = global::System.Threading.Tasks.Task.WaitAll(tasks, 1, ct); // BAN M:System.Threading.Tasks.Task.WaitAll(System.Threading.Tasks.Task[],System.Int32,System.Threading.CancellationToken)
        global::System.Threading.Tasks.Task.WaitAll(tasks, ct); // BAN M:System.Threading.Tasks.Task.WaitAll(System.Threading.Tasks.Task[],System.Threading.CancellationToken)
        _ = global::System.Threading.Tasks.Task.WaitAll(tasks, ts); // BAN M:System.Threading.Tasks.Task.WaitAll(System.Threading.Tasks.Task[],System.TimeSpan)
        _ = global::System.Threading.Tasks.Task.WaitAny(tasks); // BAN M:System.Threading.Tasks.Task.WaitAny(System.Threading.Tasks.Task[])
        _ = global::System.Threading.Tasks.Task.WaitAny(tasks, 1); // BAN M:System.Threading.Tasks.Task.WaitAny(System.Threading.Tasks.Task[],System.Int32)
        _ = global::System.Threading.Tasks.Task.WaitAny(tasks, 1, ct); // BAN M:System.Threading.Tasks.Task.WaitAny(System.Threading.Tasks.Task[],System.Int32,System.Threading.CancellationToken)
        _ = global::System.Threading.Tasks.Task.WaitAny(tasks, ct); // BAN M:System.Threading.Tasks.Task.WaitAny(System.Threading.Tasks.Task[],System.Threading.CancellationToken)
        _ = global::System.Threading.Tasks.Task.WaitAny(tasks, ts); // BAN M:System.Threading.Tasks.Task.WaitAny(System.Threading.Tasks.Task[],System.TimeSpan)
        _ = ti.Result; // BAN P:System.Threading.Tasks.Task`1.Result
        _ = vi.Result; // BAN P:System.Threading.Tasks.ValueTask`1.Result
        t.GetAwaiter().GetResult(); // BAN M:System.Runtime.CompilerServices.TaskAwaiter.GetResult
        _ = ti.GetAwaiter().GetResult(); // BAN M:System.Runtime.CompilerServices.TaskAwaiter`1.GetResult
        t.ConfigureAwait(false).GetAwaiter().GetResult(); // BAN M:System.Runtime.CompilerServices.ConfiguredTaskAwaitable.ConfiguredTaskAwaiter.GetResult
        _ = ti.ConfigureAwait(false).GetAwaiter().GetResult(); // BAN M:System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1.ConfiguredTaskAwaiter.GetResult
        v.GetAwaiter().GetResult(); // BAN M:System.Runtime.CompilerServices.ValueTaskAwaiter.GetResult
        _ = vi.GetAwaiter().GetResult(); // BAN M:System.Runtime.CompilerServices.ValueTaskAwaiter`1.GetResult
        v.ConfigureAwait(false).GetAwaiter().GetResult(); // BAN M:System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter.GetResult
        _ = vi.ConfigureAwait(false).GetAwaiter().GetResult(); // BAN M:System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable`1.ConfiguredValueTaskAwaiter.GetResult

        _ = global::System.DateTime.Now; // BAN P:System.DateTime.Now
        _ = global::System.DateTime.UtcNow; // BAN P:System.DateTime.UtcNow
        _ = global::System.DateTime.Today; // BAN P:System.DateTime.Today
        _ = global::System.DateTimeOffset.Now; // BAN P:System.DateTimeOffset.Now
        _ = global::System.DateTimeOffset.UtcNow; // BAN P:System.DateTimeOffset.UtcNow
        _ = global::System.Threading.Tasks.Task.Delay(1); // BAN M:System.Threading.Tasks.Task.Delay(System.Int32)
        _ = global::System.Threading.Tasks.Task.Delay(1, ct); // BAN M:System.Threading.Tasks.Task.Delay(System.Int32,System.Threading.CancellationToken)
        _ = global::System.Threading.Tasks.Task.Delay(ts); // BAN M:System.Threading.Tasks.Task.Delay(System.TimeSpan)
        _ = global::System.Threading.Tasks.Task.Delay(ts, ct); // BAN M:System.Threading.Tasks.Task.Delay(System.TimeSpan,System.Threading.CancellationToken)

        global::System.GC.Collect(); // BAN M:System.GC.Collect
        global::System.GC.Collect(0); // BAN M:System.GC.Collect(System.Int32)
        global::System.GC.Collect(0, global::System.GCCollectionMode.Default); // BAN M:System.GC.Collect(System.Int32,System.GCCollectionMode)
        global::System.GC.Collect(0, global::System.GCCollectionMode.Default, true); // BAN M:System.GC.Collect(System.Int32,System.GCCollectionMode,System.Boolean)
        global::System.GC.Collect(0, global::System.GCCollectionMode.Default, true, true); // BAN M:System.GC.Collect(System.Int32,System.GCCollectionMode,System.Boolean,System.Boolean)
        _ = global::System.Environment.GetEnvironmentVariable("X"); // BAN M:System.Environment.GetEnvironmentVariable(System.String)
        _ = global::System.Environment.GetEnvironmentVariable("X", global::System.EnvironmentVariableTarget.User); // BAN M:System.Environment.GetEnvironmentVariable(System.String,System.EnvironmentVariableTarget)
        _ = global::System.Environment.GetEnvironmentVariables(); // BAN M:System.Environment.GetEnvironmentVariables
        _ = global::System.Environment.GetEnvironmentVariables(global::System.EnvironmentVariableTarget.User); // BAN M:System.Environment.GetEnvironmentVariables(System.EnvironmentVariableTarget)

        _ = process.Start(); // BAN M:System.Diagnostics.Process.Start
        _ = global::System.Diagnostics.Process.Start(info); // BAN M:System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)
        _ = global::System.Diagnostics.Process.Start("x"); // BAN M:System.Diagnostics.Process.Start(System.String)
        _ = global::System.Diagnostics.Process.Start("x", arguments); // BAN M:System.Diagnostics.Process.Start(System.String,System.Collections.Generic.IEnumerable{System.String})
        _ = global::System.Diagnostics.Process.Start("x", "y"); // BAN M:System.Diagnostics.Process.Start(System.String,System.String)
        _ = global::System.Diagnostics.Process.Start("x", "y", secret, "d"); // BAN M:System.Diagnostics.Process.Start(System.String,System.String,System.Security.SecureString,System.String)
        _ = global::System.Diagnostics.Process.Start("x", "y", "z", secret, "d"); // BAN M:System.Diagnostics.Process.Start(System.String,System.String,System.String,System.Security.SecureString,System.String)
    }
}
