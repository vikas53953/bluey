using System.Runtime.CompilerServices;

namespace Boo.Core.Tests;

/// <summary>
/// The 4-core CI runner starts the thread pool with four workers and injects
/// more at roughly one per half second. Tests that park a pool thread on a
/// gate (RequestCoordinatorTests RC-004 holds two while 1,600 other cases run
/// in parallel) can then miss their 2 s signal window. Raising the floor makes
/// the pool schedule those Task.Run bodies at once; nothing in the tests
/// depends on the pool being small.
/// </summary>
internal static class TestThreadPool
{
    [ModuleInitializer]
    internal static void RaiseMinimumWorkers()
    {
        ThreadPool.GetMinThreads(out var workers, out var io);
        var floor = Math.Max(workers, 32);
        ThreadPool.SetMinThreads(floor, Math.Max(io, floor));
    }
}
