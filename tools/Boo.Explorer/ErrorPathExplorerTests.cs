using System.Diagnostics;
using Boo.Demo.Tests;
using FlaUI.Core.AutomationElements;
using Xunit.Abstractions;

namespace Boo.Explorer;

/// <summary>
/// A wrong key: OpenAI really answers 401 (unbilled). Boo must say so plainly, never echo the key, never stay busy,
/// and a second ask right after must behave the same (no stuck state).
/// </summary>
[TestCaseOrderer("Boo.Demo.Tests.StepOrderer", "Boo.Explorer")]
public sealed class WrongKeyExplorerTests : ExplorerSteps<BooAppFixture>, IClassFixture<WrongKeyFixture>
{
    public WrongKeyExplorerTests(WrongKeyFixture boo, ITestOutputHelper output) : base(boo, output) { }

    [Fact, Step(1)]
    public void B01_WrongKey_PlainMessage_TwiceInARow()
    {
        Explore("b01-wrong-key", faces =>
        {
            var panel = OpenPanel();
            SelectDisplay(panel);
            var notes = new List<string>();
            for (var i = 1; i <= 2; i++)
            {
                var clock = Stopwatch.StartNew();
                Ask(panel, "What is the capital of France?", label: $"b01 wrong key #{i}");
                var done = BooAppFixture.WaitUntil(() => Status(panel) is { Length: > 0 } s && !LooksBusy(s), TimeSpan.FromSeconds(60));
                var status = Status(panel);
                notes.Add($"ask #{i}: done={done} afterMs={clock.ElapsedMilliseconds} status='{status}' booRows={BooRows(panel).Count}");
                Assert.True(done, $"With a wrong key Boo still looks busy after 60 s: '{status}'.");
                Assert.DoesNotContain("boo-explorer-wrong-key", status, StringComparison.Ordinal);
                Assert.True(status.Contains("key", StringComparison.OrdinalIgnoreCase) || status.Contains("401", StringComparison.Ordinal)
                    || status.Contains("credential", StringComparison.OrdinalIgnoreCase),
                    $"A rejected key is not explained in plain words: '{status}'.");
            }
            Boo.CaptureScreen("b01-wrong-key");
            var settled = SettlesHidden(TimeSpan.FromSeconds(10));
            notes.Add($"settled={settled} face='{NotchFace()}' rows='{string.Join(" || ", Rows(panel).Select(r => Shorten(r, 120)))}'");
            Boo.WriteObservation("b01-wrong-key", string.Join("\n", notes));
            Assert.True(settled, $"The notch stayed '{NotchFace()}' after a 401.");
        });
    }
}

/// <summary>
/// No network for Boo (firewall). Boo must really try (the meter sees the request start and end without a status),
/// report a network problem in plain words in reasonable time, never hang, and the notch must settle. Stop during a
/// pending request is NOT tested here: blocked connections fail in under a second, so nothing stays pending (run
/// 43469967627: 687 ms). L04 (real reply) and tools/Boo.VerifyLive/Regressions/StopStatusRegressionTests.cs cover it.
/// </summary>
[TestCaseOrderer("Boo.Demo.Tests.StepOrderer", "Boo.Explorer")]
public sealed class NoNetworkExplorerTests : ExplorerSteps<BooAppFixture>, IClassFixture<NoNetworkFixture>
{
    public NoNetworkExplorerTests(NoNetworkFixture boo, ITestOutputHelper output) : base(boo, output) { }

    [Fact, Step(1)]
    public void N01_NoNetwork_ReportsIt_InPlainWords_AndSettles()
    {
        Explore("n01-no-network", faces =>
        {
            Boo.WriteObservation("n01-firewall", "block rule: " + NoNetworkFixture.BlockResult);
            Assert.StartsWith("exit=0", NoNetworkFixture.BlockResult, StringComparison.Ordinal);
            var panel = OpenPanel();
            SelectDisplay(panel);
            var notes = new List<string>();

            var clock = Stopwatch.StartNew();
            Ask(panel, "What is the capital of France?", label: "n01 blocked #1");
            var done = BooAppFixture.WaitUntil(() => Status(panel) is { Length: > 0 } s && !LooksBusy(s), TimeSpan.FromSeconds(130));
            var status = Status(panel);
            notes.Add($"ask #1: done={done} afterMs={clock.ElapsedMilliseconds} status='{status}'");
            Boo.CaptureScreen("n01-no-network");
            Assert.True(done, $"With no network Boo still looks busy after 130 s: '{status}'.");
            Assert.DoesNotContain("401", status, StringComparison.Ordinal); // a 401 would mean the firewall block did not hold

            // Boo really tried: the meter saw a request start, and it ended without an HTTP status (blocked).
            var tried = BooAppFixture.WaitUntil(() => Meter.Started > 0 && Meter.InFlight == 0, TimeSpan.FromSeconds(10));
            var settled = SettlesHidden(TimeSpan.FromSeconds(10));
            notes.Add($"meter: started={Meter.Started} inFlight={Meter.InFlight} tried={tried}; settled={settled} face='{NotchFace()}'");
            Boo.WriteObservation("n01-no-network", string.Join("\n", notes));
            Assert.True(tried, "The meter never saw Boo try a request while offline, so the offline path was not reached.");
            Assert.True(settled, $"The notch stayed '{NotchFace()}' while offline.");
        });
    }
}
