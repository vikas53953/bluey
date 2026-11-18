using System.Text.Json;
using Boo.Core;
using Boo.Providers;

namespace Boo.Integration.Tests;

public sealed class RealtimeVoiceConfigurationTests
{
    private static readonly ProviderSettings Settings = new(
        ReasoningModel: "gpt-6.1-sol", RealtimeModel: "gpt-realtime-2.1",
        FocusModel: "gpt-6-luna", Voice: "marin");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Omitted_and_null_transcription_model_keep_the_shipped_default(bool pushToTalk)
    {
        Assert.Equal("gpt-4o-mini-transcribe", Model(RealtimeProtocol.SessionUpdate(Settings, pushToTalk)));
        Assert.Equal("gpt-4o-mini-transcribe", Model(RealtimeProtocol.SessionUpdate(Settings, pushToTalk, null)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_transcription_model_is_sent_in_the_session_configuration(bool pushToTalk)
    {
        Assert.Equal("custom-transcriber", Model(RealtimeProtocol.SessionUpdate(Settings, pushToTalk, "custom-transcriber")));
    }

    [Fact]
    public void Transcription_failure_is_an_observable_event_with_its_item_identity()
    {
        var value = RealtimeProtocol.Parse("""
            {"type":"conversation.item.input_audio_transcription.failed","item_id":"spoken-turn-7","content_index":2,"error":{"type":"transcription_error","message":"unusable audio"}}
            """);
        Assert.NotNull(value);
        Assert.Equal("conversation.item.input_audio_transcription.failed", value.Kind);
        Assert.Equal("spoken-turn-7", value.ItemId);
        Assert.Equal(2, value.ContentIndex);
    }

    private static string? Model(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("session").GetProperty("audio")
            .GetProperty("input").GetProperty("transcription").GetProperty("model").GetString();
    }
}