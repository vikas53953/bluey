using System.Diagnostics;
using Boo.Demo.Tests;
using Boo.LiveSmoke;
using FlaUI.Core.AutomationElements;

namespace Boo.QaSuite.Journeys;

/// <summary>Typed branch of j18. Two typed sends, no retries; provider overlap, rings and face ownership remain unverified.</summary>
public sealed class RequestGenerationTests : IClassFixture<LiveAppFixture>
{
    private readonly LiveAppFixture _boo;
    public RequestGenerationTests(LiveAppFixture boo) => _boo = boo;

    [Fact]
    public void Q18_NewerTypedRequestWins_OnlyItsAnswerAppearsDuringLateResultWatch()
    {
        using var recorder = new NotchStateRecorder(_boo, "request-generations");
        var panel = QaUi.OpenPanel(_boo);
        var clock = Stopwatch.StartNew();
        var log = new List<string>();
        void Record(string value) => log.Add($"{clock.ElapsedMilliseconds} {value}");
        IReadOnlyList<string> Answers()
        {
            var list = panel.FindFirstDescendant(cf => cf.ByAutomationId("TranscriptList"));
            Assert.NotNull(list);
            return list.FindAllChildren(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem))
                .Select(item => item.Properties.Name.Value)
                .Where(name => name.StartsWith("Boo: ", StringComparison.Ordinal))
                .Select(name => name[5..]).ToArray();
        }
        try
        {
            var before = Answers().Count;
            Record("first typed Send");
            QaUi.Ask(_boo, panel, "Write a 400-word story about a lighthouse keeper and a lost cat.");
            Assert.True(BooAppFixture.WaitUntil(() =>
                QaUi.NameOf(panel, "StatusText").StartsWith("Thinking about", StringComparison.Ordinal)
                && Answers().Count == before, TimeSpan.FromSeconds(5)),
                "SETUP: first typed send was not observed pending in the UI with no answer before replacement.");
            Record("first typed send pending in UI, no assistant answer; provider start not proven");
            var token = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var input = QaUi.Find(_boo, panel, "InputBox").AsTextBox();
            input.Text = $"Copy this token verbatim with no spaces or punctuation. Return only the token: {token}";
            // Recheck immediately before Send: text editing must not consume the first request's entire lifetime.
            Assert.Equal(before, Answers().Count);
            Assert.StartsWith("Thinking about", QaUi.NameOf(panel, "StatusText"), StringComparison.Ordinal);
            Record("second typed Send, expected=" + token);
            QaUi.Find(_boo, panel, "SendButton").AsButton().Invoke();
            Assert.True(BooAppFixture.WaitUntil(() => Answers().Count > before, TimeSpan.FromSeconds(120)),
                $"No second answer. Status: {QaUi.NameOf(panel, "StatusText")}");
            var after = Answers().Skip(before).ToArray();
            Assert.Equal(token, Assert.Single(after).Trim());
            Record("only second answer appeared=" + after[0]);
            _boo.CaptureScreen("qa-q18-second-answer");

            var deadline = clock.ElapsedMilliseconds + 15_000;
            do
            {
                var replies = Answers().Skip(before).ToArray();
                Assert.Equal(token, Assert.Single(replies).Trim());
                Record("late-result watch: one answer=" + replies[0]);
                Thread.Sleep(100);
            } while (clock.ElapsedMilliseconds < deadline);
            _boo.CaptureScreen("qa-q18-after-late-result-watch");
            Record("15-second watch complete; no obsolete transcript row observed. First-provider overlap, stale-provider-result rejection, rings and face ownership not proven by this check.");
        }
        catch (Exception ex)
        {
            Record("FAILED: " + ex);
            try { _boo.CaptureScreen("qa-q18-FAILED"); }
            catch (Exception captureError) { Record("failure screenshot unavailable: " + captureError); }
            throw;
        }
        finally
        {
            try { Record("transcript=" + string.Join(" || ", Answers())); }
            catch (Exception diagnosticError) { Record("final transcript diagnostic failed: " + diagnosticError); }
            _boo.WriteObservation("qa-q18-newer-typed-request", string.Join("\n", log));
        }
    }
}