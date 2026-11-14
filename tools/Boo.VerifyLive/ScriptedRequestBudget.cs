using System.IO;

namespace Boo.Explorer;

public static class ExplorerLedger
{
    private static readonly object Gate = new();
    private static int _admitted;

    static ExplorerLedger() => AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        RequestMeter.DrainAll();
        Flush();
    };

    public static void Begin(int pid, string step)
    {
        var meter = RequestMeter.For(pid, "scripted-j0-j5", true);
        if (meter.Problem is { } problem) throw new InvalidOperationException(problem);
        if (!meter.Drain(TimeSpan.FromSeconds(30)))
            throw new InvalidOperationException("Scripted previous request still pending");
        if (step is "j2-chat" or "j3-explain" or "j4-research" or "j5-apply-undo")
            Admit(step, meter, true);
        Flush();
    }

    public static void Admit(string what, RequestMeter meter, bool billable)
    {
        lock (Gate)
        {
            if (_admitted >= 4) throw new InvalidOperationException("Scripted budget exhausted before " + what);
            _admitted++;
            meter.AllowOne();
        }
    }

    public static void Flush()
    {
        lock (Gate)
        {
            var dir = Environment.GetEnvironmentVariable("BOO_UI_ARTIFACTS");
            if (string.IsNullOrWhiteSpace(dir)) return;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "scripted-budget.txt"),
                $"admitted={_admitted}/4; real-key starts={RequestMeter.BillableStarted}; estimated USD={RequestMeter.BillableStarted * 0.004m:F4} (workload proxy, not billing)\n" +
                "meter problems=" + string.Join(";", RequestMeter.Problems) + "\n" + string.Join("\n", RequestMeter.Events));
        }
    }
}
