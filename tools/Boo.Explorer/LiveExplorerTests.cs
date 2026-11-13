using System.IO;
using Boo.Core;
using Boo.Demo.Tests;
using Boo.LiveSmoke;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using Xunit.Abstractions;

namespace Boo.Explorer;

/// <summary>
/// REAL key, real OpenAI (cheapest chat model, set by the workflow): chat, then the variations that need a reply in
/// flight. Planned: 11 real-key requests of the cap 20; each is admitted before its question (ExplorerLedger) and
/// checked at Boo's network boundary (RequestMeter); nothing retries. Every "while Boo is answering" step first proves
/// THIS question's request is on the wire.
/// Each answer carries a code word so a stale or misplaced reply is unambiguous. Replies the explorer interrupts are
/// ~120 words long, so they are still on the wire when it acts (one-word replies finish within a second).
/// </summary>
[TestCaseOrderer("Boo.Demo.Tests.StepOrderer", "Boo.Explorer")]
public sealed class LiveExplorerTests : ExplorerSteps<BooAppFixture>, IClassFixture<LiveAppFixture>
{
    public LiveExplorerTests(LiveAppFixture boo, ITestOutputHelper output) : base(boo, output) { }

    protected override bool BillableLaunch => true;

    private string WaitForBooRowContaining(Window panel, string word, TimeSpan timeout) =>
        Boo.WaitFor(() => BooRows(panel).LastOrDefault(r => r.Contains(word, StringComparison.OrdinalIgnoreCase)), timeout,
            $"a Boo answer containing {word}");

    [Fact, Step(1)]
    public void L01_Chat_RealAnswer_NotchSettles()
    {
        Explore("l01-chat", faces =>
        {
            var panel = OpenPanel();
            SelectDisplay(panel);
            Ask(panel, "Reply with exactly the word ALPHA.", label: "l01 chat");
            var reply = WaitForBooRowContaining(panel, "ALPHA", AnswerTimeout);
            Boo.CaptureScreen("l01-chat");
            var settled = BooAppFixture.WaitUntil(() => !LooksBusy(Status(panel)), UiTimeout) && SettlesHidden(TimeSpan.FromSeconds(12));
            Boo.WriteObservation("l01-chat", $"reply='{Shorten(reply, 200)}' status='{Status(panel)}' settled={settled} faces={string.Join(",", faces.Faces.Distinct())}");
            Assert.True(settled, $"After a real answer the notch stayed '{NotchFace()}' / status '{Status(panel)}'.");
        });
    }

    [Fact, Step(2)]
    public void L02_TypingDuringPendingReply_NewerQuestionWins_NoStaleAnswer()
    {
        Explore("l02-newer-wins", faces =>
        {
            var panel = OpenPanel();
            Ask(panel, "Write a 120-word paragraph about the Moon, then end with the word BRAVO.", label: "l02 first (to be superseded)");
            RequireReplyPending(panel, "BRAVO", "l02 first question still pending");
            Ask(panel, "Reply with exactly the word CHARLIE.", label: "l02 second (typed during the pending reply)");
            WaitForBooRowContaining(panel, "CHARLIE", AnswerTimeout);
            Thread.Sleep(15_000); // a late BRAVO would land in this window
            var rows = Rows(panel);
            Boo.CaptureScreen("l02-newer-wins");
            var you = rows.Count(r => r.StartsWith("You: ", StringComparison.Ordinal) && (r.Contains("BRAVO") || r.Contains("CHARLIE")));
            var staleBravo = BooRows(panel).Any(r => r.Contains("BRAVO", StringComparison.OrdinalIgnoreCase) || r.Contains("Moon", StringComparison.OrdinalIgnoreCase));
            Boo.WriteObservation("l02-newer-wins", $"rows='{string.Join(" || ", rows.TakeLast(6).Select(r => Shorten(r, 120)))}'\nyouRows={you} staleBravo={staleBravo} status='{Status(panel)}'");
            Assert.Equal(2, you);
            Assert.False(staleBravo, "The superseded question's answer (BRAVO) still appeared after a newer question was sent.");
            Assert.False(LooksBusy(Status(panel)), $"Status stuck busy: '{Status(panel)}'.");
        });
    }

    [Fact, Step(3)]
    public void L03_SettingsSavedMidChat_ReplyStillArrives()
    {
        Explore("l03-settings-mid-chat", faces =>
        {
            var panel = OpenPanel();
            Ask(panel, "Write a 120-word paragraph about rivers, then end with the word DELTA.", label: "l03 settings saved mid-reply");
            RequireReplyPending(panel, "DELTA", "l03 reply pending while settings are saved");
            var chirp = Find(panel, "ChirpMute").AsCheckBox();
            chirp.IsChecked = chirp.IsChecked != true;
            Find(panel, "SaveSettingsButton").AsButton().Invoke();
            var statusAfterSave = Status(panel);
            var reply = WaitForBooRowContaining(panel, "DELTA", AnswerTimeout);
            Boo.CaptureScreen("l03-settings-mid-chat");
            var keyFile = File.Exists(Path.Combine(Boo.DataDirectory, AiProviders.CredentialFileName(AiProvider.OpenAI)));
            var speechOff = System.Text.RegularExpressions.Regex.IsMatch(BooAppFixture.ReadShared(Boo.SettingsPath), "\"SpeechEnabled\"\\s*:\\s*false",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            Boo.WriteObservation("l03-settings-mid-chat", $"statusAfterSave='{statusAfterSave}' reply='{Shorten(reply, 200)}' finalStatus='{Status(panel)}' keyFileKept={keyFile} speechStillOff={speechOff}");
            Assert.True(keyFile, "Saving settings mid-chat removed the saved key.");
            Assert.True(speechOff, "Saving settings mid-chat switched speech on (typed questions would go to voice).");
        });
    }

    [Fact, Step(4)]
    public void L04_StopMidReply_LateAnswerDropped_ThenRecovers()
    {
        Explore("l04-stop-mid-reply", faces =>
        {
            var panel = OpenPanel();
            var before = BooRows(panel).Count;
            Ask(panel, "Write a 150-word story about a lighthouse keeper, and end it with the word ECHO.", label: "l04 stopped mid-reply");
            RequireReplyPending(panel, "lighthouse", "l04 reply pending when Stop is pressed");
            PressStop();
            var stopped = BooAppFixture.WaitUntil(() => !LooksBusy(Status(panel)), TimeSpan.FromSeconds(5));
            var statusAfterStop = Status(panel);
            Thread.Sleep(25_000); // the provider's answer would arrive in this window
            var statusAfter25s = Status(panel);
            Boo.CaptureScreen("l04-25s-after-stop");
            var late = BooRows(panel).Skip(before).Any(r => r.Contains("lighthouse", StringComparison.OrdinalIgnoreCase) || r.Contains("ECHO", StringComparison.OrdinalIgnoreCase));
            var settled = SettlesHidden(TimeSpan.FromSeconds(8));
            Ask(panel, "Reply with exactly the word FOXTROT.", label: "l04 recovery after Stop");
            var reply = WaitForBooRowContaining(panel, "FOXTROT", AnswerTimeout);
            Boo.CaptureScreen("l04-after-stop");
            Boo.WriteObservation("l04-stop-mid-reply", $"stoppedWithin5s={stopped} statusAfterStop='{statusAfterStop}' statusAfter25s='{statusAfter25s}' lateAnswerShown={late} settled={settled} recoveryReply='{Shorten(reply, 120)}'");
            Assert.True(stopped, $"Stop did not end the pending reply within 5 s: '{statusAfterStop}'.");
            Assert.False(late, "The stopped question's answer still appeared after Stop.");
            Assert.True(settled, $"After Stop the notch stayed '{NotchFace()}'.");
        });
    }

    [Fact, Step(5)]
    public void L05_HoverAndHotkeyStormDuringReply_ReplyArrives_OnePanel()
    {
        Explore("l05-storm", faces =>
        {
            var panel = OpenPanel();
            Ask(panel, "Write a 120-word paragraph about mountains, then end with the word GOLF.", label: "l05 hover/hotkey storm during reply");
            RequireReplyPending(panel, "GOLF", "l05 reply pending during the storm");
            var rng = Rng("l05");
            var hover = NotchHoverPoint();
            var away = AwayPoint();
            for (var i = 0; i < 12; i++)
            {
                Mouse.MoveTo(i % 2 == 0 ? hover : away);
                if (rng.Next(3) == 0) BooAppFixture.PressOpenShortcut();
                Thread.Sleep(rng.Next(50, 350));
            }
            panel = Boo.WaitForPanel(UiTimeout);
            var reply = WaitForBooRowContaining(panel, "GOLF", AnswerTimeout);
            var settled = BooAppFixture.WaitUntil(() => !LooksBusy(Status(panel)), UiTimeout) && SettlesHidden(TimeSpan.FromSeconds(12));
            Boo.CaptureScreen("l05-storm");
            Boo.WriteObservation("l05-storm", $"reply='{Shorten(reply, 120)}' settled={settled} faces={faces.Timeline()}");
            Assert.True(settled, $"After the storm the notch stayed '{NotchFace()}'.");
        });
    }

    [Fact, Step(6)]
    public void L06_NewQuestionWhileApprovalCardShows_CardGone_NothingWritten()
    {
        Explore("l06-card-then-new-question", faces =>
        {
            var panel = OpenPanel();
            var folder = Path.Combine(Path.GetTempPath(), "boo-explorer-task-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "boo-explorer.txt");
            Find(panel, "FileRootBox").AsTextBox().Text = folder;
            Find(panel, "SaveSettingsButton").AsButton().Invoke();
            Assert.True(BooAppFixture.WaitUntil(() => Status(panel).StartsWith("Settings saved", StringComparison.Ordinal), UiTimeout),
                $"Settings were not saved: '{Status(panel)}'.");

            Ask(panel, "create a file named boo-explorer.txt in the task folder containing exactly the text EXPLORER-OK", label: "l06 file task (card)");
            var card = BooAppFixture.WaitUntil(() => Visible(TryFind(panel, "ActionPreview")) is not null, AnswerTimeout);
            Boo.CaptureScreen("l06-card");
            if (!card)
            {
                Boo.WriteObservation("l06-card-then-new-question", $"AI-DEPENDENT: no Review card appeared; status='{Status(panel)}' lastRows='{string.Join(" || ", BooRows(panel).TakeLast(2))}'");
                Assert.Fail("AI-DEPENDENT (not a Boo verdict): the model did not propose the file action, so the card edge was not reached.");
            }
            Ask(panel, "Reply with exactly the word HOTEL.", label: "l06 new question while the card shows");
            var cardGone = BooAppFixture.WaitUntil(() => Visible(TryFind(panel, "ActionPreview")) is null, TimeSpan.FromSeconds(10));
            var reply = WaitForBooRowContaining(panel, "HOTEL", AnswerTimeout);
            var approveClickable = Visible(TryFind(panel, "ApproveButton")) is not null && IsEnabled(TryFind(panel, "ApproveButton"));
            Thread.Sleep(2000);
            var written = File.Exists(file);
            Boo.CaptureScreen("l06-after-new-question");
            Boo.WriteObservation("l06-card-then-new-question", $"cardGoneWithin10s={cardGone} approveClickable={approveClickable} fileWritten={written} reply='{Shorten(reply, 120)}' status='{Status(panel)}'");
            Assert.True(cardGone, "The old Review card is still showing after a new question was sent.");
            Assert.False(approveClickable, "The old card can still be approved after a new question.");
            Assert.False(written, "A file was written without Approve.");
        });
    }

    [Fact, Step(7)]
    public void L07_KillMidReply_RelaunchOnSameData_ChatWorks()
    {
        Explore("l07-kill-relaunch", faces =>
        {
            var panel = OpenPanel();
            Ask(panel, "Write a 120-word paragraph about tea, then end with the word INDIA.", label: "l07 killed mid-reply");
            RequireReplyPending(panel, "INDIA", "l07 reply pending when Boo is killed");
            KilledOnPurpose = true;
            Boo.App.Kill();
            Assert.True(BooAppFixture.WaitUntil(() => Boo.App.HasExited, TimeSpan.FromSeconds(10)), "Boo did not exit on kill.");
            var copy = Path.Combine(Path.GetTempPath(), "boo-explorer-l07-" + Guid.NewGuid().ToString("N"));
            NoKeyExplorerTests.CopyDir(Boo.DataDirectory, copy);

            using var again = new RelaunchFixture(copy);
            var first = Boo;
            UseApp(again);
            try
            {
                var p2 = OpenPanel(again);
                SelectDisplay(p2);
                Thread.Sleep(1500);
                var health = HealthProblems(again);
                var crashNotice = Status(p2);
                Ask(p2, "Reply with exactly the word JULIET.", label: "l07 after relaunch");
                var reply = Boo.WaitFor(() => BooRows(p2).LastOrDefault(r => r.Contains("JULIET", StringComparison.OrdinalIgnoreCase)), AnswerTimeout, "JULIET after relaunch");
                // Before this Boo is closed, its last request's STOP must be in the ledger.
                Assert.True(Meter.Drain(TimeSpan.FromSeconds(15)), $"The relaunched Boo still has {Meter.InFlight} request(s) in flight.");
                again.CaptureScreen("l07-after-relaunch");
                again.WriteObservation("l07-kill-relaunch", $"health='{string.Join("; ", health.DefaultIfEmpty("ok"))}' statusOnRelaunch='{crashNotice}' reply='{Shorten(reply, 120)}'");
                Assert.Empty(health);
                Assert.Empty(HealthProblems(again));
            }
            finally
            {
                UseApp((BooAppFixture)first);
            }
        });
    }
}
