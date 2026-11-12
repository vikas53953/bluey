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
public sealed record LiveConfig(AiProvider TextProvider, IReadOnlyDictionary<AiProvider, string> Keys, string? Model)
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

        var keys = new Dictionary<AiProvider, string>();
        foreach (var candidate in AiProviders.All)
        {
            var value = Environment.GetEnvironmentVariable(KeyVariable(candidate));
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (AiProviders.CredentialRejectReason(candidate, value) is { } reason)
            {
                throw new InvalidOperationException(
                    $"LIVE SMOKE FAILED CLOSED: the {KeyVariable(candidate)} secret is not a usable key: {reason}");
            }
            keys[candidate] = value.Trim();
        }

        if (!keys.ContainsKey(provider))
        {
            throw new InvalidOperationException(
                $"LIVE SMOKE FAILED CLOSED: text provider is {AiProviders.DisplayName(provider)} but the repository secret " +
                $"{KeyVariable(provider)} is missing or empty. Add it under Settings > Secrets and variables > Actions " +
                "(see docs/LIVE_AI_SMOKE.md). This run is not a pass.");
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

        return new LiveConfig(provider, keys, model);
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
/// The demo harness's fixture, with Boo's data folder pre-seeded before launch: each provided key goes through Boo's
/// own DPAPI <see cref="CredentialStore"/> (so no key is ever typed into the UI or visible in a screenshot) and
/// settings.json selects the text provider. The temp data folder is deleted when the fixture is disposed.
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
        foreach (var (provider, key) in Config.Keys)
        {
            new CredentialStore(Path.Combine(dataDirectory, AiProviders.CredentialFileName(provider))).Save(key);
        }
        new SettingsStore(Path.Combine(dataDirectory, "settings.json")).Save(new AppSettings(Provider: Config.ProviderSettings()));
    }
}

/// <summary>
/// One real round trip: typed question -> Boo captures the screen -> real provider -> answer row in Boo's transcript.
/// Boo appends a "Boo: ..." transcript row only when the provider call succeeded; every failure (bad key, no quota,
/// offline, model unavailable) goes to the status line instead, so the row is the pass signal and the status line is
/// the failure evidence.
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
            var answered = BooAppFixture.WaitUntil(() => (reply = BooReply(panel)) is not null, AnswerTimeout);
            var status = NameOfId(panel, "StatusText");
            var annotation = NameOfId(panel, "AnnotationDetail");
            _boo.CaptureScreen("live-01-after-answer");
            _boo.WriteObservation("live-01-round-trip",
                $"provider={provider} model={config.Model ?? "<Boo default>"} answered={answered} " +
                $"replyLength={reply?.Length ?? 0}\nstatus='{status}'\nannotation='{annotation}'\nreply='{Shorten(reply, 600)}'");

            Assert.True(answered,
                $"No answer from {provider} within {AnswerTimeout.TotalSeconds:0}s. Boo's status line says: '{status}'");
            Assert.False(string.IsNullOrWhiteSpace(reply), "Boo's answer row is empty.");
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
