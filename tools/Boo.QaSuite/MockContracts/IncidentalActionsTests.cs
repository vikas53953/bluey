using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Boo.Core;
using Boo.Demo.Tests;
using Boo.Storage;
using FlaUI.Core.AutomationElements;

namespace Boo.QaSuite.MockContracts;

/// <summary>Mock only: unauthorized provider actions must be refused without exposing approval.</summary>
public sealed class IncidentalActionsTests : IClassFixture<IncidentalFixture>
{
    private readonly IncidentalFixture _boo;
    public IncidentalActionsTests(IncidentalFixture boo) => _boo = boo;
    private const string Question = "What does edit mean?";
    private const string Answer = "QA-EDIT: I propose replacing the button text in MainWindow.xaml with QA-EDIT-APPLIED.";
    private const string Refusal = "Boo suggested an action, but no explicit task request authorized it.";

    [Fact]
    public void Q15E_PlainQuestion_RejectsActualIncidentalAction_WithoutApprovalOrFileChange()
    {
        Assert.True(BooAppFixture.OnGitHubHostedRunner());
        var clock = Stopwatch.StartNew();
        var log = new List<string>();
        var violations = new List<string>();
        void Record(string text) => log.Add($"{clock.ElapsedMilliseconds} {text}");
        var root = Path.Combine(_boo.DataDirectory, "Boo.QaWorkspace");
        Dictionary<string, byte[]> Inventory() => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(root, p), File.ReadAllBytes, StringComparer.Ordinal);
        var baseline = Inventory();
        try
        {
            var panel = QaUi.OpenPanelOnceReady(_boo, Record);
            AutomationElement Find(string id) => panel.FindFirstDescendant(cf => cf.ByAutomationId(id))
                ?? throw new InvalidOperationException("Missing " + id);
            var mockLog = Environment.GetEnvironmentVariable("BOO_QA_MOCK_LOG")
                ?? throw new InvalidOperationException("Mock receipt path required");
            var offset = BooAppFixture.ReadShared(mockLog).Length;
            void Observe()
            {
                var start = clock.ElapsedMilliseconds;
                foreach (var id in new[] { "ActionPreview", "ApproveButton", "NextStepButton" })
                {
                    var element = panel.FindFirstDescendant(cf => cf.ByAutomationId(id));
                    if (element is not null && !element.IsOffscreen && element.BoundingRectangle.Width > 0)
                        violations.Add($"{start}: exposed {id}");
                }
                var next = panel.FindFirstDescendant(cf => cf.ByAutomationId("ActionPreviewText"));
                if (next is not null && !next.IsOffscreen && next.Properties.Name.Value.StartsWith("Next step", StringComparison.Ordinal))
                    violations.Add($"{start}: exposed Next step");
                var now = Inventory();
                if (!baseline.Keys.Order().SequenceEqual(now.Keys.Order()) || baseline.Any(p => !now.TryGetValue(p.Key, out var bytes) || !p.Value.SequenceEqual(bytes)))
                    violations.Add($"{start}: workspace inventory/bytes changed");
                Record($"negative observation [{start},{clock.ElapsedMilliseconds}]");
            }
            string Status() => Find("StatusText").Properties.Name.Value;
            bool AnswerSeen() => Find("TranscriptList").FindAllChildren()
                .Any(row => row.Properties.Name.Value == "Boo: " + Answer);
            var input = Find("InputBox").AsTextBox();
            input.Text = Question;
            Observe();
            Record("one plain-question Send");
            Find("SendButton").AsButton().Invoke();
            long? completed = null;
            do
            {
                Observe();
                var ready = AnswerSeen() && !Status().StartsWith("Thinking", StringComparison.Ordinal);
                if (ready) completed ??= clock.ElapsedMilliseconds;
                else completed = null;
                if (completed is not null && clock.ElapsedMilliseconds - completed.Value >= 3000) break;
                Thread.Sleep(40);
            } while (clock.Elapsed < TimeSpan.FromSeconds(120));
            Record("actual status=" + Status());
            Record("interval violations=" + string.Join("; ", violations));
            _boo.CaptureScreen("qa-j15e-completed");
            RequireCapture("qa-j15e-completed");
            Assert.True(completed is not null && clock.ElapsedMilliseconds - completed.Value >= 3000, "No stable completed exact answer");
            var receipt = BooAppFixture.ReadShared(mockLog)[offset..];
            Assert.DoesNotContain("<truncated>", receipt, StringComparison.Ordinal);
            var entries = Regex.Matches(receipt, @"(?ms)^\d{4}-\d\d-\d\dT[^\r\n ]+ \[(request|response)\] ([^\r\n]*)\r?\n(.*?)(?=^\d{4}-\d\d-\d\dT[^\r\n ]+ \[|\z)").Cast<Match>().ToArray();
            var request = Assert.Single(entries, e => e.Groups[1].Value == "request" && e.Groups[2].Value == "POST /v1/responses");
            using var requestJson = JsonDocument.Parse(request.Groups[3].Value);
            var questions = requestJson.RootElement.GetProperty("input").EnumerateArray()
                .Where(x => x.GetProperty("role").GetString() == "user")
                .SelectMany(x => x.GetProperty("content").EnumerateArray())
                .Where(x => x.GetProperty("type").GetString() == "input_text")
                .Select(x => x.GetProperty("text").GetString()).ToArray();
            Assert.Single(questions, q => q is not null && q.StartsWith("User request: " + Question + "\n", StringComparison.Ordinal));
            var responseIndex = Array.IndexOf(entries, request) + 1;
            Assert.InRange(responseIndex, 0, entries.Length - 1);
            var response = entries[responseIndex];
            Assert.Equal("response", response.Groups[1].Value);
            Assert.Equal("/v1/responses", response.Groups[2].Value);
            using var envelope = JsonDocument.Parse(response.Groups[3].Value);
            var texts = envelope.RootElement.GetProperty("output").EnumerateArray()
                .Where(x => x.GetProperty("type").GetString() == "message")
                .SelectMany(x => x.GetProperty("content").EnumerateArray())
                .Where(x => x.GetProperty("type").GetString() == "output_text")
                .Select(x => x.GetProperty("text").GetString()).ToArray();
            using var payload = JsonDocument.Parse(Assert.Single(texts)!);
            var action = Assert.Single(payload.RootElement.GetProperty("actions").EnumerateArray());
            Assert.Equal("write_file", action.GetProperty("name").GetString());
            Assert.Equal("MainWindow.xaml", action.GetProperty("target").GetString());
            Record("paired actual request/response: one write_file MainWindow.xaml; exact answer received");
            Record($"workspace files={baseline.Count}; raw bytes unchanged during bounded polling; no approval violations={violations.Count == 0}");
            _boo.WriteObservation("qa-j15e-incidental-actions", string.Join("\n", log));
            Assert.Empty(violations);
            Assert.Equal(Refusal, Status());
        }
        catch (Exception ex)
        {
            Record("FAILED: " + ex);
            _boo.CaptureScreen("qa-j15e-FAILED");
            RequireCapture("qa-j15e-FAILED");
            throw;
        }
        finally { _boo.WriteObservation("qa-j15e-incidental-actions", string.Join("\n", log)); }
    }

    private void RequireCapture(string moment)
    {
        var manifest = File.ReadAllText(Path.Combine(_boo.ArtifactsDirectory, moment + ".capture.txt"));
        var images = Regex.Matches(manifest, @"(?m)^boo-(\d+):").Cast<Match>().ToArray();
        Assert.NotEmpty(images);
        foreach (var image in images)
        {
            using var bitmap = new Bitmap(Path.Combine(_boo.ArtifactsDirectory, moment + ".boo-" + image.Groups[1].Value + ".png"));
            Assert.True(bitmap.Width > 0 && bitmap.Height > 0);
        }
    }
}

public sealed class IncidentalFixture : BooAppFixture
{
    public IncidentalFixture() : base(Seed) { }
    private static void Seed(string data)
    {
        if (!OnGitHubHostedRunner()) throw new InvalidOperationException("GitHub-hosted mock runner required");
        var source = Environment.GetEnvironmentVariable("BOO_DEMO_WORKSPACE") ?? throw new InvalidOperationException("Workspace fixture required");
        var root = Path.Combine(data, "Boo.QaWorkspace");
        Directory.CreateDirectory(root);
        foreach (var path in Directory.GetDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(root, Path.GetRelativePath(source, path)));
        foreach (var path in Directory.GetFiles(source, "*", SearchOption.AllDirectories)) File.Copy(path, Path.Combine(root, Path.GetRelativePath(source, path)));
        new CredentialStore(Path.Combine(data, AiProviders.CredentialFileName(AiProvider.OpenAI))).Save("sk-qa-mock-key-000000000000000000000000");
        new SettingsStore(Path.Combine(data, "settings.json")).Save(new AppSettings(SpeechEnabled: false, ChirpsEnabled: false,
            FileRoot: root, Provider: new ProviderSettings(ReasoningModel: "gpt-6-luna")));
    }
}
