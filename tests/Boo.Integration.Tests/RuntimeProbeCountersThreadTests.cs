using System.Runtime.CompilerServices;
using Boo.Core;
using Boo.Windows;

namespace Boo.Integration.Tests;

/// <summary>
/// The perf probe reads JIT counters for the WHOLE process, not only the thread that writes the snapshot: in Boo the
/// writer is a timer thread, while the compiling happens on the UI thread and the runtime's background tiering thread
/// (issue #124). Codex mutation 14/16 on 1b1260d: switching either reader call to <c>currentThread: true</c> survived,
/// because the other tests compile on the reading thread. Here the compiling happens on a separate thread and the
/// reading thread must still see it, measured against what that thread itself reported.
/// </summary>
public sealed class RuntimeProbeCountersThreadTests
{
    [Fact]
    public void Jit_methods_compiled_on_another_thread_are_counted()
    {
        OnWorker(() => { }); // warm the thread start/join path on this thread first
        var before = RuntimeProbeCounters.Read(new ProbeWriteMeter());
        var worker = OnWorker(() => Touch<A0>() + Touch<A1>() + Touch<A2>() + Touch<A3>() + Touch<A4>() +
                                    Touch<A5>() + Touch<A6>() + Touch<A7>() + Touch<A8>() + Touch<A9>());
        var after = RuntimeProbeCounters.Read(new ProbeWriteMeter());

        Assert.True(worker.Methods >= 10, $"the worker compiled {worker.Methods} methods, expected at least 10");
        Assert.True(after.JitMethods - before.JitMethods >= worker.Methods,
            $"process-wide JIT methods rose {after.JitMethods - before.JitMethods}, but the worker alone compiled {worker.Methods}");
    }

    [Fact]
    public void Jit_time_spent_on_another_thread_is_counted()
    {
        OnWorker(() => { });
        var before = RuntimeProbeCounters.Read(new ProbeWriteMeter());
        var worker = OnWorker(() => Touch<B0>() + Touch<B1>() + Touch<B2>() + Touch<B3>() + Touch<B4>() +
                                    Touch<B5>() + Touch<B6>() + Touch<B7>() + Touch<B8>() + Touch<B9>());
        var after = RuntimeProbeCounters.Read(new ProbeWriteMeter());

        Assert.True(worker.Time > TimeSpan.Zero, "the worker's own JIT time did not move");
        Assert.True(after.JitTime - before.JitTime >= worker.Time,
            $"process-wide JIT time rose {(after.JitTime - before.JitTime).TotalMilliseconds} ms, but the worker alone " +
            $"spent {worker.Time.TotalMilliseconds} ms");
    }

    /// <summary>Runs <paramref name="work"/> on a new thread and returns what that thread itself compiled.</summary>
    private static (long Methods, TimeSpan Time) OnWorker(Func<int> work)
    {
        long methods = 0;
        var time = TimeSpan.Zero;
        var thread = new Thread(() =>
        {
            var m0 = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true);
            var t0 = System.Runtime.JitInfo.GetCompilationTime(currentThread: true);
            GC.KeepAlive(work());
            methods = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true) - m0;
            time = System.Runtime.JitInfo.GetCompilationTime(currentThread: true) - t0;
        });
        thread.Start();
        thread.Join();
        return (methods, time);
    }

    private static (long Methods, TimeSpan Time) OnWorker(Action work) => OnWorker(() => { work(); return 0; });

    // Each value-type instantiation is its own compiled method, so every Touch<T> below is compiled fresh, once.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Touch<T>() where T : struct => Unsafe.SizeOf<T>() + default(T).GetHashCode() % 2;

    private struct A0 { } private struct A1 { } private struct A2 { } private struct A3 { } private struct A4 { }
    private struct A5 { } private struct A6 { } private struct A7 { } private struct A8 { } private struct A9 { }
    private struct B0 { } private struct B1 { } private struct B2 { } private struct B3 { } private struct B4 { }
    private struct B5 { } private struct B6 { } private struct B7 { } private struct B8 { } private struct B9 { }
}
