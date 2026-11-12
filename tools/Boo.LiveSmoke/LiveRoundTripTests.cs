using System.IO;
using Boo.Core;
using Boo.Demo.Tests;
using Boo.Storage;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Boo.LiveSmoke;

/// <summary>
/// What the live run needs, read from the environment the workflow sets. Fails closed: a missing or malformed key
/// throws with a plain message (never the key itself), so the run goes red instead of skipping as green.
/// </summary>
public sealed record LiveConfig(AiProvider TextProvider, string Key, string? Model)
{
    public const string ProviderVariable = "BOO_LIVE_TEXT_PROVIDER";
    public const string ModelVariable = "BOO_LIVE_MODEL";

    public static string KeyVariable(AiProvider provider) => provider switch
    {
        AiProvider.OpenAI => "BOO_LIVE_OPENAI_KEY",
        AiProvider.Anthropic => "BOO_LIVE_ANTHROPIC_KEY",
        AiProvider.Gemini => "BOO_LIVE_GEMINI_KEY",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    public static LiveConfig FromEnvironment()
    {
        var providerText = (Environment.GetEnvironmentVariable(ProviderVariable) ?? string.Empty).Trim();
        var provider = providerText.ToLowerInvariant() switch
        {
            "" or "openai" => AiProvider.OpenAI,
            "anthropic" or "claude" => AiProvider.Anthropic,
            "gemini" or "google" => AiProvider.Gemini,
            _ => throw new InvalidOperationException(
                $"LIVE SMOKE FAILED CLOSED: {ProviderVariable} must be openai, anthropic or gemini (got '{providerText}')."),
        };

        // Only the chosen provider's key is read and seeded. Any other key in Boo's data folder would let a different
        // service (for example OpenAI voice, which always uses the OpenAI key) answer and turn the run into a false green.
        var value = Environment.GetEnvironmentVariable(KeyVariable(provider));
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"LIVE SMOKE FAILED CLOSED: text provider is {AiProviders.DisplayName(provider)} but the repository secret " +
                $"{KeyVariable(provider)} is missing or empty. Add it under Settings > Secrets and variables > Actions " +
                "(see docs/LIVE_AI_SMOKE.md). This run is not a pass.");
        }
        if (AiProviders.CredentialRejectReason(provider, value) is { } reason)
        {
            throw new InvalidOperationException(
                $"LIVE SMOKE FAILED CLOSED: the {KeyVariable(provider)} secret is not a usable key: {reason}");
        }

        var model = Environment.GetEnvironmentVariable(ModelVariable)?.Trim();
        if (string.IsNullOrEmpty(model))
        {
            model = null;
        }
        else if (!AiProviders.IsValidModelId(model))
        {
            throw new InvalidOperationException($"LIVE SMOKE FAILED CLOSED: {ModelVariable} '{model}' is not a valid model id.");
        }

        return new LiveConfig(provider, value.Trim(), model);
    }

    /// <summary>The settings Boo starts with: the chosen text provider, and the model override for it if one was given.</summary>
    public ProviderSettings ProviderSettings()
    {
        var settings = new ProviderSettings(TextProvider: TextProvider);
        if (Model is null) return settings;
        return TextProvider switch
        {
            AiProvider.OpenAI => settings with { ReasoningModel = Model },
            AiProvider.Anthropic => settings with { AnthropicModel = Model },
            AiProvider.Gemini => settings with { GeminiModel = Model },
            _ => settings,
        };
    }
}

/// <summary>
/// The demo harness's fixture, with Boo's data folder pre-seeded before launch: the chosen provider's key (and only
/// that one) goes through Boo's own DPAPI <see cref="CredentialStore"/> (so no key is ever typed into the UI or visible
/// in a screenshot), and settings.json selects the text provider with speech OFF. With speech on, Boo hands a typed
/// question to the OpenAI voice conversation instead of the text provider, which would test the wrong path.
/// The temp data folder is deleted when the fixture is disposed.
/// </summary>
public sealed class LiveAppFixture : BooAppFixture
{
    public LiveAppFixture()
        : base(Seed)
    {
    }

    private static LiveConfig? s_config;

    // Not a static initializer: a throw there would surface as a bare TypeInitializationException.
    public static LiveConfig Config => s_config ??= LiveConfig.FromEnvironment();

    private static void Seed(string dataDirectory)
    {
        // Anti-mock guard, before Boo starts: every AI host must resolve only to public internet addresses and no proxy
        // may sit in between. Loopback, LAN/private, link-local, unique-local, CGNAT, IPv6-wrapped forms of them or a
        // proxy fail the run closed. What it accepted goes into the evidence (the workflow prints every .txt there).
        var accepted = RealAiHostGuard.EnsureRealProviderHosts();
        var evidence = Environment.GetEnvironmentVariable("BOO_UI_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(evidence))
        {
            Directory.CreateDirectory(evidence);
            File.WriteAllLines(Path.Combine(evidence, "live-00-anti-mock-guard.txt"),
                new[] { "anti-mock guard ran before Boo started: no proxy, every AI host public" }.Concat(accepted));
        }

        new CredentialStore(Path.Combine(dataDirectory, AiProviders.CredentialFileName(Config.TextProvider))).Save(Config.Key);
        new SettingsStore(Path.Combine(dataDirectory, "settings.json"))
            .Save(new AppSettings(SpeechEnabled: false, Provider: Config.ProviderSettings()));
    }
}

/// <summary>
/// One real round trip: typed question -> Boo captures the screen -> chosen text provider -> answer in Boo's transcript.
/// Pass needs BOTH a "Boo: ..." transcript row AND the annotation outcome line: Boo's screen-help text path
/// (RenderReply with a captured frame) always writes that line. A plain voice reply never does; the only voice route
/// that writes it (the voice screen-help tool) needs speech on plus a Talk click, and even then calls the chosen text
/// provider with the screenshot. Speech is seeded off and the test never clicks Talk, so together they prove the
/// answer came from the chosen provider with the screenshot. Every provider failure (bad key, no quota, offline,
/// model unavailable) goes to the status line instead, which is the failure evidence. Any status mentioning the
/// voice conversation fails the run outright.
/// </summary>
public sealed class LiveRoundTripTests : IClassFixture<LiveAppFixture>
{
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(150);
    private const string Question = "Explain what is on my screen in one short sentence.";

    private readonly LiveAppFixture _boo;
    private readonly ITestOutputHelper _output;

    public LiveRoundTripTests(LiveAppFixture boo, ITestOutputHelper output)
    {
        _boo = boo;
        _output = output;
    }

    [Fact]
    public void TypedQuestion_GetsAnAnswerFromTheRealProvider()
    {
        var config = LiveAppFixture.Config;
        var provider = AiProviders.DisplayName(config.TextProvider);
        try
        {
            _boo.WaitForNotch(UiTimeout);
            var panel = _boo.EnsurePanel();
            SelectFirstDisplay(panel);
            _boo.CaptureScreen("live-00-panel-open");

            var input = Find(panel, "InputBox").AsTextBox();
            input.Text = Question;
            Find(panel, "SendButton").AsButton().Invoke();
            _output.WriteLine($"Sent to {provider}: {Question}");

            string? reply = null;
            var statuses = new List<string>();
            var answered = BooAppFixture.WaitUntil(() =>
            {
                var now = NameOfId(panel, "StatusText");
                if (now.Length > 0 && (statuses.Count == 0 || statuses[^1] != now)) statuses.Add(now);
                return (reply = BooReply(panel)) is not null && NameOfId(panel, "AnnotationDetail").Length > 0;
            }, AnswerTimeout);
            var status = NameOfId(panel, "StatusText");
            var annotation = NameOfId(panel, "AnnotationDetail");
            reply ??= BooReply(panel);
            _boo.CaptureScreen("live-01-after-answer");
            _boo.WriteObservation("live-01-round-trip",
                $"provider={provider} model={config.Model ?? "<Boo default>"} answered={answered} " +
                $"replyLength={reply?.Length ?? 0}\nstatusesSeen='{string.Join(" > ", statuses)}'\nstatus='{status}'\n" +
                $"annotation='{annotation}'\nreply='{Shorten(reply, 600)}'");

            Assert.DoesNotContain(statuses, s => s.Contains("voice conversation", StringComparison.OrdinalIgnoreCase));

            // Each failure names its real cause: no answer at all, versus an answer without the screenshot-path line.
            Assert.True(reply is not null,
                $"No answer from {provider} within {AnswerTimeout.TotalSeconds:0}s. Boo's status line says: '{status}'");
            Assert.False(string.IsNullOrWhiteSpace(reply), "Boo's answer row is empty.");
            Assert.True(annotation.Length > 0,
                $"An answer arrived but Boo wrote no screen-explanation line, so the answer is not proven to come from " +
                $"{provider} with the screenshot (most likely the screen capture failed; check live-01-after-answer.png). " +
                $"Status: '{status}'");
            Assert.True(answered, "Answer and screen-explanation line were not both present at the same time.");
            // The CI mock (tools/Boo.QaMockAi) prefixes every scripted answer with "QA-". A real model never does.
            Assert.False(reply!.StartsWith("QA-", StringComparison.Ordinal),
                $"The answer came from the QA mock, not the real provider: '{Shorten(reply, 120)}'");
            _output.WriteLine($"{provider} answered ({reply.Length} chars): {Shorten(reply, 300)}");
        }
        catch (Exception ex)
        {
            _boo.CaptureScreen("live-FAILED");
            _boo.WriteObservation("live-FAILED", ex.Message);
            throw;
        }
    }

    /// <summary>Text of the first transcript row named "Boo: ...", or null while none has arrived.</summary>
    private static string? BooReply(Window panel)
    {
        var list = TryFind(panel, "TranscriptList");
        if (list is null) return null;
        foreach (var item in list.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)))
        {
            var name = BooAppFixture.SafeName(item);
            if (name.StartsWith("Boo: ", StringComparison.Ordinal))
            {
                return name["Boo: ".Length..];
            }
        }
        return null;
    }

    /// <summary>Screen help needs a selected display; the seeded settings leave it unset, so pick the first.</summary>
    private void SelectFirstDisplay(Window panel)
    {
        var combo = TryFind(panel, "DisplayBox")?.AsComboBox();
        if (combo is null) return;
        try
        {
            if (combo.SelectedItem is null && combo.Items.Length > 0)
            {
                combo.Select(0);
                Thread.Sleep(500);
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"DisplayBox selection skipped: {ex.Message}");
        }
    }

    private static string Shorten(string? text, int max) =>
        text is null ? "<none>" : text.Length <= max ? text : text[..max] + "...";

    private static AutomationElement? TryFind(Window panel, string automationId) =>
        panel.FindFirstDescendant(cf => cf.ByAutomationId(automationId));

    private AutomationElement Find(Window panel, string automationId) =>
        _boo.WaitFor(() => TryFind(panel, automationId), UiTimeout, $"element '{automationId}'");

    private static string NameOfId(Window panel, string automationId) =>
        TryFind(panel, automationId) is { } e ? BooAppFixture.SafeName(e) : string.Empty;
}
