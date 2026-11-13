using System.Windows;
using Boo.Core;
using Boo.Providers;

namespace Boo.App;

/// <summary>
/// The Conversation list: typed and spoken lines, voice transcripts updated in place, and scrolling to the newest row.
/// Every row is its own item (<see cref="TranscriptLine"/> is a class, not a record) so screen readers report each one
/// and scrolling lands on the newest copy of a repeated line (issue #73).
/// </summary>
public partial class MainWindow
{
    private void AppendTranscript(string speaker, string text)
    {
        if (text.Length == 0)
        {
            return;
        }
        _transcript.Add(new TranscriptLine(speaker, text));
        ScrollTranscriptToEnd();
    }

    private void ApplyRealtimeTranscript(RealtimeEvent value)
    {
        var update = _realtimeTranscript.Apply(value);
        if (update is null)
        {
            return;
        }
        if (update.Speaker == "Boo" && update.Text.Length > 0)
        {
            // Boo's spoken words are typed into the notch while it is responding.
            SetFace(_faceInputs with { Caption = update.Text });
        }
        if (update.Text.Length == 0 && value.Kind is not
            ("conversation.item.input_audio_transcription.completed" or
             "response.output_audio_transcript.done" or "response.output_text.done"))
        {
            return;
        }

        var key = (update.ItemId, update.ContentIndex, update.Speaker);
        if (_realtimeTranscriptRows.TryGetValue(key, out var index))
        {
            if (update.Text.Length == 0)
            {
                RemoveRealtimeTranscriptRow(key, index);
            }
            else
            {
                _transcript[index] = new TranscriptLine(update.Speaker, update.Text);
            }
        }
        else if (update.Text.Length > 0)
        {
            _realtimeTranscriptRows.Add(key, _transcript.Count);
            AppendTranscript(update.Speaker, update.Text);
            return;
        }
        ScrollTranscriptToEnd();
    }

    private void ScrollTranscriptToEnd()
    {
        if (_transcript.Count > 0)
        {
            // The non-virtualizing list generates the new container synchronously but has not measured it
            // yet; without UpdateLayout, ScrollIntoView would scroll to a stale zero-sized rect.
            TranscriptList.UpdateLayout();
            TranscriptList.ScrollIntoView(_transcript[^1]);
        }
    }

    private void RemoveRealtimeTranscriptRow((string ItemId, int ContentIndex, string Speaker) key, int index)
    {
        _transcript.RemoveAt(index);
        _realtimeTranscriptRows.Remove(key);
        foreach (var laterKey in _realtimeTranscriptRows.Keys.ToArray())
        {
            if (_realtimeTranscriptRows[laterKey] > index)
            {
                _realtimeTranscriptRows[laterKey]--;
            }
        }
    }

    /// <summary>
    /// One Conversation row. A class, not a record: every row must be its own item. Two equal records collapsed into
    /// ONE row for screen readers (WPF keys a list item's automation peer by the item) and made ScrollIntoView jump to
    /// the older copy (issue #73).
    /// </summary>
    private sealed class TranscriptLine(string speaker, string text)
    {
        public string Speaker { get; } = speaker;
        public string Text { get; } = text;
    }
}
