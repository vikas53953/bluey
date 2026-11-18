using System.IO;
using System.Text.Json;

namespace Boo.NativeVoice;

/// <summary>One line of Boo's hosted virtual output receipt (HostedVoiceAudio): PCM bytes consumed so far, how many of
/// them were non-zero, PCM bytes still queued in Boo's playback buffer, and a monotonic clock (Environment.TickCount64).</summary>
public sealed record PlaybackReceipt(long RenderedBytes, long NonzeroBytes, long BufferedBytes, long AtMs)
{
    public static PlaybackReceipt From(JsonElement line) => new(
        line.GetProperty("renderedBytes").GetInt64(), line.GetProperty("nonzeroBytes").GetInt64(),
        line.GetProperty("bufferedBytes").GetInt64(), line.GetProperty("atMs").GetInt64());

    /// <summary>The last complete line of a receipt file (read from the end; the file grows every 20 ms); null if none yet.</summary>
    public static PlaybackReceipt? ReadLast(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(Math.Max(0, stream.Length - 8192), SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        var end = text.LastIndexOf('\n');
        if (end < 0) return null;
        var line = text[..end].Split('\n')[^1];
        if (!line.TrimStart().StartsWith('{')) return null;
        using var doc = JsonDocument.Parse(line);
        return From(doc.RootElement);
    }

    public override string ToString() => $"rendered={RenderedBytes} nonzero={NonzeroBytes} buffered={BufferedBytes} atMs={AtMs}";
}

/// <summary>
/// PR #186 review (Codex BLOCK on 4d30786): proof that a Stop interrupted playback, measured in PCM bytes (time), never in
/// non-zero bytes. Before the Stop, at least <see cref="MinQueuedBytes"/> of audio must still be queued (so a Stop after
/// the speech ended naturally fails as TEST SETUP); after it, a fresh receipt must show the queue empty and at most
/// <see cref="MaxConsumedAfterStopBytes"/> more consumed.
/// </summary>
public static class RecoveredStopCheck
{
    public const int BytesPerSecond = 48000;                        // 24 kHz, 16-bit, mono
    public const long MinQueuedBytes = BytesPerSecond / 2;          // 0.5 s still to play when Stop is pressed
    public const long MaxConsumedAfterStopBytes = BytesPerSecond / 5; // at most 0.2 s more after Stop

    /// <summary>Null when Stop is pressed while enough audio is still queued; otherwise why the run proves nothing.</summary>
    public static string? PreconditionFailure(PlaybackReceipt? beforeStop) =>
        beforeStop is null ? "TEST SETUP: no playback receipt before Stop."
        : beforeStop.BufferedBytes < MinQueuedBytes
            ? $"TEST SETUP: only {beforeStop.BufferedBytes} bytes ({beforeStop.BufferedBytes * 1000 / BytesPerSecond} ms) were still queued at Stop, below {MinQueuedBytes}; the speech had (nearly) finished, so this Stop cannot prove an interruption. {beforeStop}"
            : null;

    /// <summary>Null when the queued audio was dropped at once; otherwise what kept playing.</summary>
    public static string? InterruptionFailure(PlaybackReceipt beforeStop, PlaybackReceipt? afterStop)
    {
        if (afterStop is null || afterStop.AtMs <= beforeStop.AtMs)
            return $"No playback receipt was written after Stop, so nothing shows the queue was dropped. Before: {beforeStop}";
        var consumed = afterStop.RenderedBytes - beforeStop.RenderedBytes;
        if (consumed > MaxConsumedAfterStopBytes)
            return $"Playback continued after Stop: {consumed} bytes ({consumed * 1000 / BytesPerSecond} ms) more were consumed. Before: {beforeStop}; after: {afterStop}";
        if (afterStop.BufferedBytes != 0)
            return $"Audio was still queued after Stop ({afterStop.BufferedBytes} bytes). Before: {beforeStop}; after: {afterStop}";
        return null;
    }
}
