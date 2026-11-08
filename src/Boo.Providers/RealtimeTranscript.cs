namespace Boo.Providers;

public sealed record TranscriptUpdate(string ItemId, int ContentIndex, string Speaker, string Text);

public sealed class RealtimeTranscript
{
    private enum Channel
    {
        Input,
        Audio,
        Text
    }

    private readonly record struct ChannelKey(string ItemId, int ContentIndex, Channel Channel);

    private sealed class ChannelState
    {
        public string Text { get; set; } = string.Empty;
        public bool Finalized { get; set; }
    }

    private readonly Dictionary<ChannelKey, ChannelState> _channels = new();

    public TranscriptUpdate? Apply(RealtimeEvent value)
    {
        (Channel Channel, bool IsCompletion)? eventKind = value.Kind switch
        {
            "conversation.item.input_audio_transcription.delta" => (Channel.Input, false),
            "conversation.item.input_audio_transcription.completed" => (Channel.Input, true),
            "response.output_audio_transcript.delta" => (Channel.Audio, false),
            "response.output_audio_transcript.done" => (Channel.Audio, true),
            "response.output_text.delta" => (Channel.Text, false),
            "response.output_text.done" => (Channel.Text, true),
            _ => null
        };
        if (eventKind is not { } kind || string.IsNullOrWhiteSpace(value.ItemId) || value.Text is null || value.ContentIndex < 0)
            return null;

        var (channel, isCompletion) = kind;
        var key = new ChannelKey(value.ItemId, value.ContentIndex, channel);

        if (channel == Channel.Text && HasStartedAudio(value.ItemId, value.ContentIndex))
            return null;

        if (!_channels.TryGetValue(key, out var state))
        {
            state = new ChannelState();
            _channels.Add(key, state);
        }

        if (state.Finalized && !isCompletion)
            return null;

        if (isCompletion)
        {
            state.Text = value.Text;
            state.Finalized = true;
        }
        else
        {
            state.Text += value.Text;
        }

        return new TranscriptUpdate(
            value.ItemId,
            value.ContentIndex,
            channel == Channel.Input ? "You" : "Boo",
            state.Text);
    }

    public void Reset() => _channels.Clear();

    private bool HasStartedAudio(string itemId, int contentIndex) =>
        _channels.ContainsKey(new ChannelKey(itemId, contentIndex, Channel.Audio));
}
