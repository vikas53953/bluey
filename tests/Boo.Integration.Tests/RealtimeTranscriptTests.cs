using Boo.Providers;

namespace Boo.Integration.Tests;

public sealed class RealtimeTranscriptTests
{
    [Fact]
    public void Apply_supports_all_six_transcript_delta_and_completion_events()
    {
        var cases = new[]
        {
            (new RealtimeEvent(
                "conversation.item.input_audio_transcription.delta",
                Text: "hello",
                ItemId: "user-delta"), "You"),
            (new RealtimeEvent(
                "conversation.item.input_audio_transcription.completed",
                Text: "hello there",
                ItemId: "user-completed"), "You"),
            (new RealtimeEvent(
                "response.output_audio_transcript.delta",
                Text: "spoken",
                ItemId: "audio-delta"), "Boo"),
            (new RealtimeEvent(
                "response.output_audio_transcript.done",
                Text: "spoken answer",
                ItemId: "audio-done"), "Boo"),
            (new RealtimeEvent(
                "response.output_text.delta",
                Text: "written",
                ItemId: "text-delta",
                ContentIndex: 1), "Boo"),
            (new RealtimeEvent(
                "response.output_text.done",
                Text: "written answer",
                ItemId: "text-done",
                ContentIndex: 2), "Boo")
        };
        var reducer = new RealtimeTranscript();

        foreach (var (value, speaker) in cases)
        {
            var update = reducer.Apply(value);

            Assert.NotNull(update);
            Assert.Equal(value.ItemId, update!.ItemId);
            Assert.Equal(value.ContentIndex, update.ContentIndex);
            Assert.Equal(speaker, update.Speaker);
            Assert.Equal(value.Text, update.Text);
        }
    }

    [Fact]
    public void Delta_then_corrected_completion_replaces_one_item_with_final_text()
    {
        var reducer = new RealtimeTranscript();
        var rows = new Dictionary<(string ItemId, int ContentIndex, string Speaker), string>();
        AddToRows(reducer, rows, new RealtimeEvent(
            "conversation.item.input_audio_transcription.delta",
            Text: "The old draft",
            ItemId: "turn-1"));
        var accumulated = reducer.Apply(new RealtimeEvent(
            "conversation.item.input_audio_transcription.delta",
            Text: " is incomplete",
            ItemId: "turn-1"));
        Assert.NotNull(accumulated);
        Assert.Equal("The old draft is incomplete", accumulated!.Text);
        ApplyUpdate(rows, accumulated, isCompletion: false);

        var final = reducer.Apply(new RealtimeEvent(
            "conversation.item.input_audio_transcription.completed",
            Text: "The corrected turn.",
            ItemId: "turn-1"));
        Assert.NotNull(final);
        ApplyUpdate(rows, final!, isCompletion: true);

        var row = Assert.Single(rows);
        Assert.Equal(("turn-1", 0, "You"), row.Key);
        Assert.Equal("The corrected turn.", row.Value);
    }

    [Fact]
    public void Same_speaker_items_accumulate_independently_across_interleaved_deltas()
    {
        var reducer = new RealtimeTranscript();

        var firstItem = reducer.Apply(new RealtimeEvent(
            "conversation.item.input_audio_transcription.delta",
            Text: "first",
            ItemId: "user-1"));
        Assert.NotNull(firstItem);
        Assert.Equal("user-1", firstItem!.ItemId);
        Assert.Equal("You", firstItem.Speaker);
        Assert.Equal("first", firstItem.Text);

        var secondItem = reducer.Apply(new RealtimeEvent(
            "conversation.item.input_audio_transcription.delta",
            Text: "second",
            ItemId: "user-2"));
        Assert.NotNull(secondItem);
        Assert.Equal("user-2", secondItem!.ItemId);
        Assert.Equal("You", secondItem.Speaker);
        Assert.Equal("second", secondItem.Text);

        var firstContinuation = reducer.Apply(new RealtimeEvent(
            "conversation.item.input_audio_transcription.delta",
            Text: " item",
            ItemId: "user-1"));
        Assert.NotNull(firstContinuation);
        Assert.Equal("user-1", firstContinuation!.ItemId);
        Assert.Equal("first item", firstContinuation.Text);

        var secondContinuation = reducer.Apply(new RealtimeEvent(
            "conversation.item.input_audio_transcription.delta",
            Text: " item",
            ItemId: "user-2"));
        Assert.NotNull(secondContinuation);
        Assert.Equal("user-2", secondContinuation!.ItemId);
        Assert.Equal("second item", secondContinuation.Text);
    }

    [Fact]
    public void Interleaved_items_and_content_indexes_keep_independent_rows()
    {
        var rows = ApplyToRows(
            new RealtimeEvent(
                "conversation.item.input_audio_transcription.delta",
                Text: "first user turn",
                ItemId: "user-1"),
            new RealtimeEvent(
                "response.output_text.delta",
                Text: "first answer",
                ItemId: "assistant-1"),
            new RealtimeEvent(
                "conversation.item.input_audio_transcription.delta",
                Text: "second content part",
                ItemId: "user-1",
                ContentIndex: 1),
            new RealtimeEvent(
                "response.output_text.done",
                Text: "corrected first answer",
                ItemId: "assistant-1"),
            new RealtimeEvent(
                "conversation.item.input_audio_transcription.completed",
                Text: "corrected first user turn",
                ItemId: "user-1"));

        Assert.Equal(3, rows.Count);
        Assert.Equal("corrected first user turn", rows[("user-1", 0, "You")]);
        Assert.Equal("second content part", rows[("user-1", 1, "You")]);
        Assert.Equal("corrected first answer", rows[("assistant-1", 0, "Boo")]);
    }

    [Fact]
    public void Completion_without_delta_and_repeated_final_are_one_harmless_row()
    {
        var rows = ApplyToRows(
            new RealtimeEvent(
                "response.output_text.done",
                Text: "complete without a delta",
                ItemId: "assistant-1",
                ContentIndex: 2),
            new RealtimeEvent(
                "response.output_text.done",
                Text: "complete without a delta",
                ItemId: "assistant-1",
                ContentIndex: 2));

        var row = Assert.Single(rows);
        Assert.Equal(("assistant-1", 2, "Boo"), row.Key);
        Assert.Equal("complete without a delta", row.Value);
    }

    [Fact]
    public void Late_delta_after_completion_is_ignored()
    {
        var rows = ApplyToRows(
            new RealtimeEvent(
                "response.output_audio_transcript.done",
                Text: "final spoken answer",
                ItemId: "assistant-1"),
            new RealtimeEvent(
                "response.output_audio_transcript.delta",
                Text: " late fragment",
                ItemId: "assistant-1"));

        var row = Assert.Single(rows);
        Assert.Equal("final spoken answer", row.Value);
    }

    [Fact]
    public void Empty_final_clears_existing_row_without_creating_an_empty_row()
    {
        var reducer = new RealtimeTranscript();
        var rows = new Dictionary<(string ItemId, int ContentIndex, string Speaker), string>();

        AddToRows(reducer, rows, new RealtimeEvent(
            "conversation.item.input_audio_transcription.delta",
            Text: "temporary text",
            ItemId: "user-1"));
        var final = reducer.Apply(new RealtimeEvent(
            "conversation.item.input_audio_transcription.completed",
            Text: string.Empty,
            ItemId: "user-1"));

        Assert.NotNull(final);
        Assert.Equal(string.Empty, final!.Text);
        ApplyUpdate(rows, final, isCompletion: true);
        Assert.Empty(rows);

        var emptyWithoutDelta = reducer.Apply(new RealtimeEvent(
            "conversation.item.input_audio_transcription.completed",
            Text: string.Empty,
            ItemId: "user-2"));
        Assert.NotNull(emptyWithoutDelta);
        ApplyUpdate(rows, emptyWithoutDelta!, isCompletion: true);
        Assert.Empty(rows);
    }

    [Fact]
    public void Finalized_audio_transcript_wins_over_text_channel_without_concatenation()
    {
        var reducer = new RealtimeTranscript();
        var rows = new Dictionary<(string ItemId, int ContentIndex, string Speaker), string>();
        var itemId = "assistant-1";

        AddToRows(reducer, rows, new RealtimeEvent(
            "response.output_text.delta",
            Text: "draft text",
            ItemId: itemId));
        AddToRows(reducer, rows, new RealtimeEvent(
            "response.output_text.done",
            Text: "written representation",
            ItemId: itemId));
        AddToRows(reducer, rows, new RealtimeEvent(
            "response.output_audio_transcript.delta",
            Text: "spoken representation",
            ItemId: itemId));
        AddToRows(reducer, rows, new RealtimeEvent(
            "response.output_audio_transcript.done",
            Text: "spoken final",
            ItemId: itemId));

        var lateTextDelta = reducer.Apply(new RealtimeEvent(
            "response.output_text.delta",
            Text: "late written fragment",
            ItemId: itemId));
        var lateTextFinal = reducer.Apply(new RealtimeEvent(
            "response.output_text.done",
            Text: "late written final",
            ItemId: itemId));

        Assert.Null(lateTextDelta);
        Assert.Null(lateTextFinal);
        var row = Assert.Single(rows);
        Assert.Equal((itemId, 0, "Boo"), row.Key);
        Assert.Equal("spoken final", row.Value);
    }

    [Fact]
    public void Text_updates_after_audio_begins_do_not_replace_or_append_audio_text()
    {
        var reducer = new RealtimeTranscript();
        var itemId = "assistant-1";

        var audioStart = reducer.Apply(new RealtimeEvent(
            "response.output_audio_transcript.delta",
            Text: "spoken",
            ItemId: itemId));
        Assert.NotNull(audioStart);
        Assert.Equal("spoken", audioStart!.Text);

        Assert.Null(reducer.Apply(new RealtimeEvent(
            "response.output_text.delta",
            Text: "written fragment",
            ItemId: itemId)));
        Assert.Null(reducer.Apply(new RealtimeEvent(
            "response.output_text.done",
            Text: "written final",
            ItemId: itemId)));

        var audioContinuation = reducer.Apply(new RealtimeEvent(
            "response.output_audio_transcript.delta",
            Text: " reply",
            ItemId: itemId));
        Assert.NotNull(audioContinuation);
        Assert.Equal("spoken reply", audioContinuation!.Text);
    }

    [Fact]
    public void Missing_identity_unrelated_events_and_missing_text_produce_no_update()
    {
        var reducer = new RealtimeTranscript();
        var values = new[]
        {
            new RealtimeEvent(
                "conversation.item.input_audio_transcription.delta",
                Text: "text",
                ItemId: null),
            new RealtimeEvent(
                "response.output_text.done",
                Text: "text",
                ItemId: " "),
            new RealtimeEvent("response.created", ItemId: "unrelated"),
            new RealtimeEvent(
                "response.output_text.delta",
                Text: null,
                ItemId: "assistant-1"),
            new RealtimeEvent(
                "conversation.item.input_audio_transcription.completed",
                Text: null,
                ItemId: "user-1")
        };

        foreach (var value in values)
            Assert.Null(reducer.Apply(value));
    }

    [Fact]
    public void Reset_discards_completion_state_and_allows_a_new_turn_for_the_same_channel()
    {
        var reducer = new RealtimeTranscript();
        var final = new RealtimeEvent(
            "response.output_text.done",
            Text: "old final",
            ItemId: "assistant-1");

        Assert.NotNull(reducer.Apply(final));
        Assert.Null(reducer.Apply(new RealtimeEvent(
            "response.output_text.delta",
            Text: "ignored before reset",
            ItemId: "assistant-1")));

        reducer.Reset();

        var afterReset = reducer.Apply(new RealtimeEvent(
            "response.output_text.delta",
            Text: "new turn",
            ItemId: "assistant-1"));
        Assert.NotNull(afterReset);
        Assert.Equal("new turn", afterReset!.Text);
    }

    [Fact]
    public void Nonempty_whitespace_delta_is_preserved_as_text()
    {
        var update = new RealtimeTranscript().Apply(new RealtimeEvent(
            "response.output_text.delta",
            Text: "   ",
            ItemId: "assistant-1"));

        Assert.NotNull(update);
        Assert.Equal("   ", update!.Text);
    }

    [Fact]
    public void Negative_content_index_is_not_processable_by_the_reducer()
    {
        var update = new RealtimeTranscript().Apply(new RealtimeEvent(
            "response.output_text.delta",
            Text: "text",
            ItemId: "assistant-1",
            ContentIndex: -1));

        Assert.Null(update);
    }

    private static Dictionary<(string ItemId, int ContentIndex, string Speaker), string> ApplyToRows(
        params RealtimeEvent[] values)
    {
        var reducer = new RealtimeTranscript();
        var rows = new Dictionary<(string ItemId, int ContentIndex, string Speaker), string>();
        foreach (var value in values)
            AddToRows(reducer, rows, value);
        return rows;
    }

    private static void AddToRows(
        RealtimeTranscript reducer,
        Dictionary<(string ItemId, int ContentIndex, string Speaker), string> rows,
        RealtimeEvent value)
    {
        var update = reducer.Apply(value);
        if (update is not null)
            ApplyUpdate(rows, update, IsCompletion(value.Kind));
    }

    private static void ApplyUpdate(
        Dictionary<(string ItemId, int ContentIndex, string Speaker), string> rows,
        TranscriptUpdate update,
        bool isCompletion)
    {
        var key = (update.ItemId, update.ContentIndex, update.Speaker);
        if (isCompletion && update.Text.Length == 0)
            rows.Remove(key);
        else
            rows[key] = update.Text;
    }

    private static bool IsCompletion(string kind) => kind is
        "conversation.item.input_audio_transcription.completed" or
        "response.output_audio_transcript.done" or
        "response.output_text.done";
}
