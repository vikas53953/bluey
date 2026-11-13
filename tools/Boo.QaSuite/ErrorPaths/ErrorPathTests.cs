using Boo.Core;
using Boo.Demo.Tests;
using Boo.LiveSmoke;
using FlaUI.Core.AutomationElements;
using Xunit.Abstractions;

namespace Boo.QaSuite.ErrorPaths;

// Error paths: what a user sees when something is wrong. Each class gets its own fresh Boo (no pooled connection or
// cached answer carries over), with speech off so the typed path is the one tested. None of these Boo copies holds the
// real key. Expected wording is Boo's own (src/Boo.Core/ProviderFailure.cs, src/Boo.App/MainWindow.xaml.cs); a
// different message fails the check and names what Boo said.

internal static class Expect
{
    public const string TypeFirst = "Type a question first.";
    public const string NoKey = "No OpenAI key yet. Add it in Settings, then try again.";
    public static readonly string[] Timeout = ["OpenAI response timed out. Try again.", ProviderFailureClassifier.TimeoutMessage];

    public static void Status(BooAppFixture boo, Window panel, string step, IReadOnlyCollection<string> expected, TimeSpan timeout, string extra = "")
    {
        var (matched, last, seen) = QaUi.WaitForStatus(panel, expected, timeout);
        boo.CaptureScreen(step);
        boo.WriteObservation(step, $"expected one of: {string.Join(" | ", expected)}\nmatched={matched}\nlast='{last}'\nseen='{string.Join(" > ", seen)}'\nreplies={QaUi.BooRows(panel).Count}{extra}");
        Assert.True(matched, $"Boo did not show the expected message. Expected one of [{string.Join(" | ", expected)}], last status '{last}', seen '{string.Join(" > ", seen)}'.");
        Assert.Empty(QaUi.BooRows(panel));
    }
}

/// <summary>E0 empty question; E1 no key saved.</summary>
[TestCaseOrderer("Boo.Demo.Tests.StepOrderer", "Boo.QaSuite")]
public sealed class NoKeyErrorTests(NoKeyFixture boo) : IClassFixture<NoKeyFixture>
{
    [Fact, Step(0)]
    public void E0_EmptyQuestion_AsksTheUserToTypeFirst()
    {
        var panel = boo.EnsurePanel();
        QaUi.Find(boo, panel, "InputBox").AsTextBox().Text = string.Empty;
        QaUi.Find(boo, panel, "SendButton").AsButton().Invoke();
        Expect.Status(boo, panel, "qa-e0-empty-question", [Expect.TypeFirst], TimeSpan.FromSeconds(10));
    }

    [Fact, Step(1)]
    public void E1_NoKey_SaysAddAKey_AndSendsNothing()
    {
        var panel = boo.EnsurePanel();
        QaUi.Ask(boo, panel, "What is two plus three?");
        Expect.Status(boo, panel, "qa-e1-no-key", [Expect.NoKey], TimeSpan.FromSeconds(30));
    }
}

/// <summary>E2 a key OpenAI rejects: the real api.openai.com answers 401 and Boo says so in plain words.</summary>
public sealed class BadKeyErrorTests(FakeKeyFixture boo) : IClassFixture<FakeKeyFixture>
{
    [Fact]
    public void E2_BadKey_RealOpenAIRejectsIt_AndBooSaysPasteAValidKey()
    {
        // Prove the rejection comes from the real OpenAI, not a local stand-in.
        var hosts = RealAiHostGuard.EnsureRealProviderHosts();
        var panel = boo.EnsurePanel();
        QaUi.Ask(boo, panel, "What is two plus three?");
        Expect.Status(boo, panel, "qa-e2-bad-key", [ProviderFailureClassifier.InvalidCredentialMessage], TimeSpan.FromSeconds(60),
            "\nantiMockGuard=" + string.Join("; ", hosts));
    }
}

/// <summary>E3 network down (firewall blocks Boo.exe), then the network comes back and Boo reaches OpenAI again.</summary>
public sealed class NetworkDownErrorTests(FakeKeyFixture boo, ITestOutputHelper output) : IClassFixture<FakeKeyFixture>
{
    [Fact]
    public void E3_NetworkDown_SaysCheckYourInternet_ThenRecoversWhenItIsBack()
    {
        var panel = boo.EnsurePanel();
        var log = new List<string>();
        using (Faults.BlockOutbound(boo.ExePath, s => { log.Add(s); output.WriteLine(s); }))
        {
            QaUi.Ask(boo, panel, "What is two plus three?");
            Expect.Status(boo, panel, "qa-e3-network-down", [ProviderFailureClassifier.OfflineMessage], TimeSpan.FromSeconds(60),
                "\n" + string.Join("\n", log));
        }
        // Network back: the same Boo must reach the real OpenAI again (which rejects the fake key: a 401, not offline).
        QaUi.Ask(boo, panel, "What is two plus three?");
        Expect.Status(boo, panel, "qa-e3-network-back", [ProviderFailureClassifier.InvalidCredentialMessage], TimeSpan.FromSeconds(60),
            "\n" + string.Join("\n", log));
    }
}

/// <summary>E4 the provider never answers: Boo gives up with its timeout message (45 s), then recovers.</summary>
public sealed class TimeoutErrorTests(FakeKeyFixture boo, ITestOutputHelper output) : IClassFixture<FakeKeyFixture>
{
    [Fact]
    public void E4_ProviderNeverAnswers_BooTimesOutHonestly_ThenRecovers()
    {
        var panel = boo.EnsurePanel();
        var log = new List<string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using (Faults.SilentProvider(s => { log.Add(s); output.WriteLine(s); }))
        {
            QaUi.Ask(boo, panel, "What is two plus three?");
            Expect.Status(boo, panel, "qa-e4-timeout", Expect.Timeout, TimeSpan.FromSeconds(100),
                $"\nsecondsUntilMessage={clock.Elapsed.TotalSeconds:0.0}\n" + string.Join("\n", log));
        }
        QaUi.Ask(boo, panel, "What is two plus three?");
        Expect.Status(boo, panel, "qa-e4-provider-back", [ProviderFailureClassifier.InvalidCredentialMessage], TimeSpan.FromSeconds(60),
            "\n" + string.Join("\n", log));
    }
}
