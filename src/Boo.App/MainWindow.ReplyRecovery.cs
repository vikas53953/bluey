using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using Boo.Core;
using Boo.Providers;

namespace Boo.App;

/// <summary>
/// Issue #177: when a spoken reply's audio holds no speech while its transcript has words (OpenAI sometimes sends digital
/// silence with a correct transcript), Boo reads that transcript aloud with OpenAI's cheapest text-to-speech, so the
/// person still hears the answer. The text is shown as always. One recovery per reply, never when speech is muted, and a
/// Stop or a newer request before the speech arrives drops it. Each recovery is written to the trace log (lengths and
/// levels only, never the text or the key).
/// </summary>
public partial class MainWindow
{
    private const int RealtimeSampleRate = 24000;
    internal const string RecoveringReplyStatus = "OpenAI's voice reply came back silent, so Boo is reading the answer aloud.";
    internal const string RecoveredReplyStatus = "OpenAI's voice reply came back silent; Boo read the answer aloud.";
    internal const string RecoveryUnavailableStatus = "Spoken reply is unavailable; text remains visible.";

    private readonly SilentReplyRecovery _replyRecovery = new();
    /// <summary>Speech for a recovery; null means OpenAI text-to-speech. In-process tests set it (by reflection) to a recorder.</summary>
    private Func<string, CancellationToken, Task<byte[]>>? _speakText = null;

    /// <summary>Plays recovered speech; null means Boo's audio player. In-process tests set it (by reflection) to a recorder.</summary>
    private Action<byte[]>? _playReplyAudio = null;

    /// <summary>Feeds every accepted realtime event of the current reply to the silent-reply check.</summary>
    private void ObserveReplyAudio(RealtimeEvent realtimeEvent)
    {
        switch (realtimeEvent.Kind)
        {
            case "response.created":
                _replyRecovery.Begin();
                break;
            case "response.output_audio.delta" when realtimeEvent.Audio is { Length: > 0 } audio:
                _replyRecovery.Audio(audio);
                break;
            case "response.output_audio_transcript.delta":
                _replyRecovery.Transcript(realtimeEvent.Text, done: false);
                break;
            case "response.output_audio_transcript.done":
                _replyRecovery.Transcript(realtimeEvent.Text, done: true);
                break;
        }
    }

    /// <summary>At response.done: when the reply had a transcript but no speech, read the transcript aloud.</summary>
    private void RecoverSilentReply(RealtimeClient source, long generation)
    {
        if (_replyRecovery.Complete(SpeechMute.IsChecked == true, RealtimeSampleRate) is not { } decision)
        {
            return;
        }
        Trace.TraceWarning(string.Create(CultureInfo.InvariantCulture,
            $"Boo voice: silent reply recovered by text-to-speech (chars={decision.Text.Length}, peak={decision.Peak}, audioSeconds={decision.AudioSeconds:0.00}, chunks={decision.Chunks}, providerSilence={decision.ProviderSilence})."));
        StatusText.Text = RecoveringReplyStatus;
        _ = SpeakRecoveredReplyAsync(decision.Text, source, generation, _requestCancellation.Token);
    }

    private async Task SpeakRecoveredReplyAsync(string text, RealtimeClient source, long generation, CancellationToken cancellation)
    {
        byte[] audio;
        try
        {
            audio = await (_speakText ?? SpeakWithOpenAi)(text, cancellation);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (InvalidOperationException ex)
        {
            ReportRecoveryUnavailable(ex, source, generation);
            return;
        }
        catch (ArgumentException ex)
        {
            ReportRecoveryUnavailable(ex, source, generation);
            return;
        }
        if (cancellation.IsCancellationRequested || !ReferenceEquals(_realtime, source) || !IsCurrent(generation)
            || SpeechMute.IsChecked == true)
        {
            return;
        }
        var now = Environment.TickCount64;
        var start = Math.Max(now, Interlocked.Read(ref _playbackUntilMs));
        Interlocked.Exchange(ref _playbackUntilMs, start + (long)Math.Ceiling(audio.Length / PlaybackBytesPerMs));
        PlayReplyAudio(audio);
        StatusText.Text = RecoveredReplyStatus;
        Trace.TraceInformation(string.Create(CultureInfo.InvariantCulture,
            $"Boo voice: recovered speech played (bytes={audio.Length}, speechSeconds={ReplyAudio.SpeechSeconds(audio, RealtimeSampleRate):0.00})."));
    }

    private void ReportRecoveryUnavailable(Exception ex, RealtimeClient source, long generation)
    {
        Trace.TraceWarning("Boo voice: the silent-reply recovery could not get speech (" + ex.GetType().Name + ").");
        if (ReferenceEquals(_realtime, source) && IsCurrent(generation))
        {
            StatusText.Text = RecoveryUnavailableStatus;
        }
    }

    private Task<byte[]> SpeakWithOpenAi(string text, CancellationToken cancellation)
    {
        _http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        var voice = (_settings.Provider ?? new ProviderSettings()).Voice;
        return new SpeechClient(_http, () => StoreFor(AiProvider.OpenAI).Load(), voice).SpeakAsync(text, cancellation);
    }

    /// <summary>
    /// The one way reply audio reaches the speaker: each realtime audio delta and any recovered speech. When the player
    /// fails, the text stays visible and the status line says so.
    /// </summary>
    private void PlayReplyAudio(byte[] audio)
    {
        if (_playReplyAudio is { } play)
        {
            play(audio);
            return;
        }
        try
        {
            (_audio ??= CreateAudio()).Play(audio);
        }
        catch (InvalidOperationException ex)
        {
            StatusText.Text = SanitizedError(ex, RecoveryUnavailableStatus);
        }
    }
}
