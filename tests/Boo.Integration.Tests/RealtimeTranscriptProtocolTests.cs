using Boo.Providers;

namespace Boo.Integration.Tests;

public sealed class RealtimeTranscriptProtocolTests
{
    [Fact]
    public void Parse_reads_final_output_text_and_nonnegative_content_index()
    {
        var parsed = RealtimeProtocol.Parse("""
            {"type":"response.output_text.done","item_id":"assistant-1","content_index":3,"text":"Final response"}
            """);

        Assert.NotNull(parsed);
        Assert.Equal("response.output_text.done", parsed!.Kind);
        Assert.Equal("assistant-1", parsed.ItemId);
        Assert.Equal(3, parsed.ContentIndex);
        Assert.Equal("Final response", parsed.Text);
    }

    [Theory]
    [InlineData("conversation.item.input_audio_transcription.completed", "transcript", "user-1", "Spoken user turn", 0)]
    [InlineData("response.output_audio_transcript.done", "transcript", "assistant-1", "Spoken answer", 4)]
    public void Parse_reads_transcript_field_for_final_audio_channels(
        string kind,
        string field,
        string itemId,
        string text,
        int contentIndex)
    {
        var json = $$"""
            {"type":"{{kind}}","item_id":"{{itemId}}","content_index":{{contentIndex}},"{{field}}":"{{text}}"}
            """;

        var parsed = RealtimeProtocol.Parse(json);

        Assert.NotNull(parsed);
        Assert.Equal(kind, parsed!.Kind);
        Assert.Equal(itemId, parsed.ItemId);
        Assert.Equal(contentIndex, parsed.ContentIndex);
        Assert.Equal(text, parsed.Text);
    }

    [Fact]
    public void Parse_accepts_missing_content_index_as_compatible_zero()
    {
        var parsed = RealtimeProtocol.Parse("""
            {"type":"response.output_text.done","item_id":"assistant-1","text":"Final response"}
            """);

        Assert.NotNull(parsed);
        Assert.Equal(0, parsed!.ContentIndex);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"1\"")]
    [InlineData("1.5")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    [InlineData("9223372036854775807")]
    public void Parse_rejects_null_string_negative_and_overflow_content_indexes_without_throwing(string rawIndex)
    {
        var json = $$"""
            {"type":"response.output_text.done","item_id":"assistant-1","content_index":{{rawIndex}},"text":"Final response"}
            """;

        var parsed = RealtimeProtocol.Parse(json);

        Assert.Null(parsed);
    }

    [Fact]
    public void Parse_preserves_function_call_id_preference_with_content_index()
    {
        var parsed = RealtimeProtocol.Parse("""
            {"type":"response.function_call_arguments.done","item_id":"item-17","call_id":"call-42","content_index":5,"arguments":"{\"question\":\"Explain this\"}"}
            """);

        Assert.NotNull(parsed);
        Assert.Equal("call-42", parsed!.ItemId);
        Assert.Equal(5, parsed.ContentIndex);
        Assert.Equal("{\"question\":\"Explain this\"}", parsed.Text);
    }
}
