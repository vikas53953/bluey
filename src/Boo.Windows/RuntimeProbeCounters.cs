using Boo.Core;

namespace Boo.Windows;

/// <summary>
/// Reads the perf probe's process counters from the .NET runtime (issue #124): this process's user and kernel CPU time
/// (<see cref="Environment.CpuUsage"/>), every method the JIT has compiled on any thread and the time it took
/// (<see cref="System.Runtime.JitInfo"/>), and the probe's own writes from its <see cref="ProbeWriteMeter"/>.
/// Cheap and allocation-free; safe off the UI thread.
/// </summary>
public static class RuntimeProbeCounters
{
    public static PerfProbeCounters Read(ProbeWriteMeter meter)
    {
        var cpu = Environment.CpuUsage;
        return new PerfProbeCounters(
            UserCpu: cpu.UserTime,
            KernelCpu: cpu.PrivilegedTime,
            JitMethods: System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: false),
            JitTime: System.Runtime.JitInfo.GetCompilationTime(currentThread: false),
            ProbeWrites: meter.Writes,
            ProbeWriteTime: meter.Total);
    }
}
