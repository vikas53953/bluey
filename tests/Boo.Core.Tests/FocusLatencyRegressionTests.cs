using Boo.Core;

namespace Boo.Core.Tests;

/// <summary>
/// Review finding: a check's Elapsed is taken before capture and classification, and the interval restarts
/// only when the result is recorded. With real provider latency, Developer Mode checks land more than
/// 45 focus seconds apart (4 x (10 s + latency)), which used to reset the nudge run on every check.
/// </summary>
public sealed class FocusLatencyRegressionTests
{
    private static FocusCheck OffTask(double elapsed) => new(elapsed, FocusActivity.OffTask, 0.96, 0.9);

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    [InlineData(true, 8)]
    public void Sustained_off_task_still_nudges_with_classifier_latency(bool developerMode, int latencySeconds)
    {
        var controller = new FocusSessionController("write report", 1500, developerMode);
        var nudges = 0;
        for (var second = 0; second < 600 && controller.ShouldMonitor; second++)
        {
            controller.Tick(1);
            if (!controller.IsCheckDue)
                continue;

            var observedAt = controller.Snapshot.Elapsed;
            for (var wait = 0; wait < latencySeconds && controller.ShouldMonitor; wait++)
            {
                controller.Tick(1);
                second++;
            }

            if (controller.RecordCheck(OffTask(observedAt), age: latencySeconds))
                nudges++;
        }

        Assert.True(nudges >= 1, $"expected a nudge with {latencySeconds} s latency (developer mode: {developerMode})");
    }
}
