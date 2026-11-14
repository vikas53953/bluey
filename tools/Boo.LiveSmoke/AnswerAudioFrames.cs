using System.Globalization;
using System.Text.Json;
using Boo.Providers;

namespace Boo.LiveSmoke;

/// <summary>What the raw realtime messages say about the spoken answer, checked independently of the event stream.</summary>
/// <param name="Pcm">The answer audio rebuilt from the raw messages through Boo's own parser (RealtimeProtocol.Parse).</param>
/// <param name="AudioFrames">Messages of type response.output_audio.delta.</param>
/// <param name="Problems">Every way the raw audio disagrees with what Boo heard; empty when the decode path is sound.</param>
/// <param name="Lines">One line per audio message plus the session's confirmed output format, for the evidence file.</param>
/// <param name="Deltas">The base64 text of every audio message exactly as received (before decoding).</param>
public sealed record AnswerAudioReport(
    byte[] Pcm, int AudioFrames, IReadOnlyList<string> Problems, IReadOnlyList<string> Lines, IReadOnlyList<string> Deltas);

/// <summary>
/// Rebuilds the spoken answer from the raw server messages and checks every way the decode could lose or damage it:
/// an audio message Boo's parser dropped (bad base64, a bad content index), audio sent under an event name Boo does not
/// read, a chunk with an odd byte count (a 16-bit sample split across messages), and whether decoding each message
/// separately (what Boo does) gives the same bytes as decoding the joined text. Offline tests: AnswerAudioFramesTests.
/// </summary>
public static class AnswerAudioFrames
{
    public const string AudioDelta = "response.output_audio.delta";

    public static AnswerAudioReport Assemble(IEnumerable<string> rawMessages)
    {
        var pcm = new List<byte>();
        var problems = new List<string>();
        var lines = new List<string>();
        var deltas = new List<string>();
        var audioFrames = 0;
        var index = -1;
        foreach (var json in rawMessages)
        {
            index++;
            string? type;
            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(json);
                root = document.RootElement.Clone();
                type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            }
            catch (JsonException)
            {
                problems.Add($"message {index} is not JSON");
                continue;
            }

            if (type == "session.updated" && TryOutputFormat(root, out var format))
                lines.Add($"session.updated output audio format: {format}");
            if (type is null) continue;
            if (type != AudioDelta)
            {
                if (type.Contains("audio", StringComparison.Ordinal) && type.EndsWith(".delta", StringComparison.Ordinal)
                    && !type.Contains("transcript", StringComparison.Ordinal))
                    problems.Add($"message {index} carries audio as '{type}', which Boo does not read");
                continue;
            }

            audioFrames++;
            var text = root.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
            deltas.Add(text);
            var parsed = RealtimeProtocol.Parse(json);
            if (parsed?.Audio is not { } audio)
            {
                problems.Add($"audio message {index} was DROPPED by Boo's parser (base64 chars={text.Length}, chars % 4={text.Length % 4})");
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{index:000} audio DROPPED base64Chars={text.Length} mod4={text.Length % 4}"));
                continue;
            }
            if (audio.Length % 2 != 0)
                problems.Add($"audio message {index} decodes to {audio.Length} bytes: a 16-bit sample is split across messages");
            pcm.AddRange(audio);
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{index:000} audio base64Chars={text.Length} mod4={text.Length % 4} padding={text.Length - text.TrimEnd('=').Length} bytes={audio.Length} peak={SpokenAudio.PeakLevel(audio)} item={parsed.ItemId ?? "-"} content={parsed.ContentIndex}"));
        }

        var joined = string.Concat(deltas);
        try
        {
            var whole = Convert.FromBase64String(joined);
            if (!whole.AsSpan().SequenceEqual(pcm.ToArray()))
                problems.Add("decoding the joined base64 text gives different audio than decoding each message (Boo's way)");
        }
        catch (FormatException)
        {
            lines.Add("joined base64 text is not one valid base64 string (expected when each message is encoded on its own)");
        }

        lines.Insert(0, string.Create(CultureInfo.InvariantCulture,
            $"raw messages={index + 1} audio messages={audioFrames} decoded bytes={pcm.Count} peak={SpokenAudio.PeakLevel(pcm.ToArray())}"));
        return new AnswerAudioReport(pcm.ToArray(), audioFrames, problems, lines, deltas);
    }

    private static bool TryOutputFormat(JsonElement root, out string format)
    {
        format = "";
        if (root.TryGetProperty("session", out var s) && s.TryGetProperty("audio", out var a)
            && a.TryGetProperty("output", out var o) && o.TryGetProperty("format", out var f))
        {
            format = f.GetRawText();
            return true;
        }
        return false;
    }
}
