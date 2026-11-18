using System.Text.Json;
using Boo.Core;

namespace Boo.Core.Tests;

/// <summary>
/// Settings &gt; Save writes AppSettings as JSON and startup reads it back into the Text AI and Updates boxes.
/// These tests pin the serialization both lanes rely on (same options as Boo.Storage.SettingsStore).
/// </summary>
public sealed class SettingsRoundTripTests
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    [Fact]
    public void Provider_and_updates_survive_a_save_and_load()
    {
        var checkedAt = new DateTimeOffset(2026, 11, 8, 9, 30, 0, TimeSpan.Zero);
        var saved = new AppSettings(
            Provider: new ProviderSettings(TextProvider: AiProvider.Gemini, AnthropicModel: "claude-x", GeminiFocusModel: "gemini-y"),
            Updates: new UpdateSettings(Enabled: true, IncludePrerelease: true, LastCheckedUtc: checkedAt));

        var loaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved, Options), Options);

        Assert.NotNull(loaded);
        Assert.Equal(saved.Provider, loaded.Provider);
        Assert.Equal(saved.Updates, loaded.Updates);
        Assert.Equal(AiProvider.Gemini, loaded.Provider!.TextProvider);
        Assert.Equal(checkedAt, loaded.Updates!.LastCheckedUtc);
    }

    [Fact]
    public void Provider_is_stored_by_name_so_reordering_the_enum_cannot_switch_providers()
    {
        var json = JsonSerializer.Serialize(new AppSettings(Provider: new ProviderSettings(TextProvider: AiProvider.Anthropic)), Options);
        Assert.Contains("\"Anthropic\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_saved_before_the_updates_lane_load_with_updates_off()
    {
        const string legacy = """{"DisplayId":null,"SpeechEnabled":true,"Provider":{"ReasoningModel":"gpt-6.1-sol"}}""";
        var loaded = JsonSerializer.Deserialize<AppSettings>(legacy, Options);

        Assert.NotNull(loaded);
        Assert.Null(loaded.Updates);
        Assert.False(UpdatePolicy.Effective(loaded).Enabled);
        Assert.Equal(AiProvider.OpenAI, loaded.Provider!.TextProvider);
    }
}
