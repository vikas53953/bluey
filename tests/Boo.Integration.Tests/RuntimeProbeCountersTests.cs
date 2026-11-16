using System.Diagnostics;
using System.Runtime.CompilerServices;
using Boo.Core;
using Boo.Windows;

namespace Boo.Integration.Tests;

/// <summary>
/// The perf probe's reader takes real values from the .NET runtime (issue #124, PR #142): user CPU is user CPU, JIT counts
/// rise when code is compiled, and the probe-write meter is passed through. The Codex mutation run on 61361c2 swapped user
/// and kernel CPU and zeroed these fields without any test failing.
/// </summary>
public sealed class RuntimeProbeCountersTests
{
    [Fact]
    public void Busy_managed_work_is_read_as_user_cpu_not_kernel_cpu()
    {
        var before = RuntimeProbeCounters.Read(new ProbeWriteMeter());
        var start = ProcessUserCpu();
        var clock = Stopwatch.StartNew();
        double sink = 0;
        while (ProcessUserCpu() - start < TimeSpan.FromMilliseconds(400) && clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            for (var i = 1; i < 200_000; i++)
            {
                sink += Math.Sqrt(i);
            }
        }
        var after = RuntimeProbeCounters.Read(new ProbeWriteMeter());

        var user = after.UserCpu - before.UserCpu;
        var kernel = after.KernelCpu - before.KernelCpu;
        Assert.True(sink > 0);
        Assert.True(user >= TimeSpan.FromMilliseconds(200),
            $"about 400 ms of pure managed arithmetic should read as user CPU; read user {user.TotalMilliseconds} ms, kernel {kernel.TotalMilliseconds} ms");
        Assert.True(user > kernel + TimeSpan.FromMilliseconds(100),
            $"user CPU must dominate for pure arithmetic; read user {user.TotalMilliseconds} ms, kernel {kernel.TotalMilliseconds} ms");
    }

    [Fact]
    public void Compiling_a_new_method_raises_the_jit_counters()
    {
        var before = RuntimeProbeCounters.Read(new ProbeWriteMeter());
        var value = NeverCalledBefore<FreshStruct>(new FreshStruct(41));
        var after = RuntimeProbeCounters.Read(new ProbeWriteMeter());

        Assert.Equal(42, value);
        Assert.True(after.JitMethods > before.JitMethods, $"JIT methods {before.JitMethods} -> {after.JitMethods}");
        Assert.True(after.JitMethods > 0);
        Assert.True(after.JitTime > TimeSpan.Zero, $"JIT time {after.JitTime.TotalMilliseconds} ms");
        Assert.True(after.JitTime >= before.JitTime);
    }

    [Fact]
    public void The_probe_write_meter_is_passed_through()
    {
        var meter = new ProbeWriteMeter();
        meter.Record(TimeSpan.FromMilliseconds(5.5));
        meter.Record(TimeSpan.FromMilliseconds(1.25));

        var read = RuntimeProbeCounters.Read(meter);

        Assert.Equal(2, read.ProbeWrites);
        Assert.Equal(TimeSpan.FromMilliseconds(6.75), read.ProbeWriteTime);
    }

    private static TimeSpan ProcessUserCpu()
    {
        using var self = Process.GetCurrentProcess();
        return self.UserProcessorTime;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int NeverCalledBefore<T>(T item) where T : struct, IHasNumber => item.Number + 1;

    private interface IHasNumber
    {
        int Number { get; }
    }

    private readonly record struct FreshStruct(int Number) : IHasNumber;
}
