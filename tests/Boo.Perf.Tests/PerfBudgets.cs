using System.Text.Json;

namespace Boo.Perf.Tests;

/// <summary>
/// Performance budgets, loaded from budgets.json next to the test assembly or from the file named by
/// the BOO_PERF_BUDGETS environment variable. Values are generous first-pass limits to be tightened
/// once a CI baseline exists.
/// </summary>
public sealed record PerfBudgets(
    double StartupToNotchMs,
    double IdleCpuPercent,
    double IdleWorkingSetMb,
    double IdlePrivateBytesMb,
    double IdleAllocKbPerSecond,
    double IdleGen0PerMinute,
    double HiddenFramesPerSecond,
    double RevealFramesPerSecondMin)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string ResolvePath()
    {
        var configured = Environment.GetEnvironmentVariable("BOO_PERF_BUDGETS");
        return Path.GetFullPath(!string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, "budgets.json"));
    }

    public static PerfBudgets Load()
    {
        var path = ResolvePath();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Perf budgets file not found at '{path}'. Set BOO_PERF_BUDGETS or restore budgets.json.", path);
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<PerfBudgets>(json, Options)
            ?? throw new InvalidDataException($"Perf budgets file '{path}' deserialized to null.");
    }
}
