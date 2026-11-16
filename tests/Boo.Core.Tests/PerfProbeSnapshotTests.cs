using System.Text.Json;
using Boo.Core;

namespace Boo.Core.Tests;

/// <summary>
/// The BOO_PERF_DIAG snapshot format (issue #124, PR #142). Each process counter must land in its own field with its own
/// value: the Codex mutation run on 61361c2 swapped user and kernel CPU and zeroed the JIT and probe-write fields, and
/// nothing failed. Every input below is distinct, so a swap or a zero cannot pass.
/// </summary>
public sealed class PerfProbeSnapshotTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly PerfProbeCounters Counters = new(
        UserCpu: TimeSpan.FromMilliseconds(11.5),
        KernelCpu: TimeSpan.FromMilliseconds(22.25),
        JitMethods: 333,
        JitTime: TimeSpan.FromMilliseconds(44.5),
        ProbeWrites: 55,
        ProbeWriteTime: TimeSpan.FromMilliseconds(66.75));

    private static JsonElement Emit(PerfProbeCounters counters) => JsonDocument.Parse(PerfProbeSnapshot.Create(
        1, At, 1234, 3, 2, 1, 4_000_000, 2_000_000, 90_000_000, 7, false, "Hidden", 8, "SoftwareOnly", counters).ToJson()).RootElement;

    [Fact]
    public void Each_process_counter_is_written_to_its_own_field_in_milliseconds()
    {
        var json = Emit(Counters);

        Assert.Equal(11.5, json.GetProperty("userCpuMs").GetDouble());
        Assert.Equal(22.25, json.GetProperty("kernelCpuMs").GetDouble());
        Assert.Equal(333, json.GetProperty("jitMethods").GetInt64());
        Assert.Equal(44.5, json.GetProperty("jitMs").GetDouble());
        Assert.Equal(55, json.GetProperty("probeWrites").GetInt64());
        Assert.Equal(66.75, json.GetProperty("probeWriteMs").GetDouble());
    }

    [Fact]
    public void The_fields_the_perf_test_reads_keep_their_names_order_and_values()
    {
        var json = Emit(Counters);

        Assert.Equal(
            [
                "schema", "writtenAtUtc", "uptimeMs", "gen0", "gen1", "gen2", "allocatedBytes", "heapBytes", "workingSetBytes",
                "notchFrames", "notchRendering", "faceState", "pacerSkipped", "notchRenderMode",
                "userCpuMs", "kernelCpuMs", "jitMethods", "jitMs", "probeWrites", "probeWriteMs",
            ],
            json.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(1, json.GetProperty("schema").GetInt32());
        Assert.Equal(At, json.GetProperty("writtenAtUtc").GetDateTimeOffset());
        Assert.Equal(1234, json.GetProperty("uptimeMs").GetInt64());
        Assert.Equal(3, json.GetProperty("gen0").GetInt32());
        Assert.Equal(2, json.GetProperty("gen1").GetInt32());
        Assert.Equal(1, json.GetProperty("gen2").GetInt32());
        Assert.Equal(4_000_000, json.GetProperty("allocatedBytes").GetInt64());
        Assert.Equal(2_000_000, json.GetProperty("heapBytes").GetInt64());
        Assert.Equal(90_000_000, json.GetProperty("workingSetBytes").GetInt64());
        Assert.Equal(7, json.GetProperty("notchFrames").GetInt64());
        Assert.False(json.GetProperty("notchRendering").GetBoolean());
        Assert.Equal("Hidden", json.GetProperty("faceState").GetString());
        Assert.Equal(8, json.GetProperty("pacerSkipped").GetInt64());
        Assert.Equal("SoftwareOnly", json.GetProperty("notchRenderMode").GetString());
    }

    [Fact]
    public void The_write_meter_counts_every_write_and_adds_up_their_time()
    {
        var meter = new ProbeWriteMeter();
        Assert.Equal(0, meter.Writes);
        Assert.Equal(TimeSpan.Zero, meter.Total);

        meter.Record(TimeSpan.FromMilliseconds(1.5));
        meter.Record(TimeSpan.FromMilliseconds(2.25));

        Assert.Equal(2, meter.Writes);
        Assert.Equal(TimeSpan.FromMilliseconds(3.75), meter.Total);
    }
}
