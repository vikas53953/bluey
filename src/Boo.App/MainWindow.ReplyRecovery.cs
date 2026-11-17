using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using Boo.Core;
using Boo.Providers;

namespace Boo.App;

/// <summary>
/// Issue #177: when a spoken reply's audio holds no speech while its transcript has words (OpenAI sometimes sends digital
/// silence with a correct transcript), Boo reads that transcript aloud with OpenAI's cheapest text-to-speech, so the
/// person still hears the answer. The text is shown as always.
/// Each recovery has its own turn number and cancellation: a new reply or new speech input on the same session, an
/// interrupt, starting push-to-talk, stopping the voice session or Stop makes any pending recovery stale, and a stale
/// recovery never plays and never writes the status. Success is reported only when the player accepted audible speech;
/// a failed speech request, unusable speech or a player failure says "unavailable" instead. Each recovery is written to
/// the trace log (lengths and levels only, never the text or the key).
/// </summary>
public partial class MainWindow
{
    private const int RealtimeSampleRate = 24000;
    internal const string RecoveringReplyStatus = "OpenAI's voice reply came back silent, so Boo is reading the answer aloud.";
    internal const string RecoveredReplyStatus = "OpenAI's voice reply came back silent; Boo read the answer aloud.";
    internal const string RecoveryUnavailableStatus = "Spoken reply is unavailable; text remains visible.";

    private readonly SilentReplyRecovery _replyRecovery = new(RealtimeSampleRate);
    private CancellationTokenSource? _recoveryCancellation;
    private long _recoveryTurn;

    /// <summary>Speech for a recovery; null means OpenAI text-to-speech. In-process tests set it (by reflection) to a recorder.</summary>
    private Func<string, CancellationToken, Task<byte[]>>? _speakText = null;

    /// <summary>Plays reply audio; null means Boo's audio player. In-process tests set it (by reflection) to a recorder.</summary>
    private Action<byte[]>? _playReplyAudio = null;

    /// <summary>Feeds every accepted realtime event to the silent-reply check; a new reply or new input ends any pending recovery.</summary>
    private void ObserveReplyAudio(RealtimeEvent realtimeEvent)
    {
        switch (realtimeEvent.Kind)
        {
            case "response.created":
                CancelPendingRecovery();
                _replyRecovery.Begin();
                break;
            case "input_audio_buffer.speech_started":
            case "input_audio_buffer.committed":
            case "disconnect":
            case "error":
                CancelPendingRecovery();
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

    /// <summary>Makes any pending recovery stale: it will neither play nor touch the status.</summary>
    private void CancelPendingRecovery()
    {
        _recoveryTurn++;
        var pending = _recoveryCancellation;
        _recoveryCancellation = null;
        pending?.Cancel();
        pending?.Dispose();
    }

    /// <summary>At response.done: when the reply had a transcript but no speech, read the transcript aloud.</summary>
    private void RecoverSilentReply(RealtimeClient source, long generation)
    {
        if (_replyRecovery.Complete(SpeechMute.IsChecked == true, RealtimeSampleRate) is not { } decision)
        {
            return;
        }
        CancelPendingRecovery();
        var turn = _recoveryTurn;
        _recoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(_requestCancellation.Token);
        Trace.TraceWarning(string.Create(CultureInfo.InvariantCulture,
            $"Boo voice: silent reply, reading the transcript aloud (chars={decision.Text.Length}, peak={decision.Peak}, audioSeconds={decision.AudioSeconds:0.00}, chunks={decision.Chunks}, providerSilence={decision.ProviderSilence})."));
        StatusText.Text = RecoveringReplyStatus;
        _ = SpeakRecoveredReplyAsync(decision.Text, source, generation, turn, _recoveryCancellation.Token);
    }

    private bool RecoveryIsCurrent(RealtimeClient source, long generation, long turn, CancellationToken cancellation) =>
        turn == _recoveryTurn && !cancellation.IsCancellationRequested && ReferenceEquals(_realtime, source) && IsCurrent(generation);

    private async Task SpeakRecoveredReplyAsync(string text, RealtimeClient source, long generation, long turn, CancellationToken cancellation)
    {
        byte[] audio;
        try
        {
            audio = await (_speakText ?? SpeakWithOpenAi)(text, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException ex)
        {
            ReportRecoveryUnavailable(ex, source, generation, turn, cancellation);
            return;
        }
        catch (InvalidOperationException ex)
        {
            ReportRecoveryUnavailable(ex, source, generation, turn, cancellation);
            return;
        }
        catch (ArgumentException ex)
        {
            ReportRecoveryUnavailable(ex, source, generation, turn, cancellation);
            return;
        }
        catch (HttpRequestException ex)
        {
            ReportRecoveryUnavailable(ex, source, generation, turn, cancellation);
            return;
        }
        catch (IOException ex)
        {
            ReportRecoveryUnavailable(ex, source, generation, turn, cancellation);
            return;
        }
        if (!RecoveryIsCurrent(source, generation, turn, cancellation) || SpeechMute.IsChecked == true)
        {
            return;
        }
        var speech = ReplyAudio.SpeechSeconds(audio, RealtimeSampleRate);
        if (speech <= 0)
        {
            Trace.TraceWarning(string.Create(CultureInfo.InvariantCulture,
                $"Boo voice: the recovery speech held no speech either (bytes={audio.Length}, peak={ReplyAudio.PeakLevel(audio)})."));
            StatusText.Text = RecoveryUnavailableStatus;
            return;
        }
        var now = Environment.TickCount64;
        var start = Math.Max(now, Interlocked.Read(ref _playbackUntilMs));
        Interlocked.Exchange(ref _playbackUntilMs, start + (long)Math.Ceiling(audio.Length / PlaybackBytesPerMs));
        if (!PlayReplyAudio(audio))
        {
            Trace.TraceWarning("Boo voice: the recovery speech could not be played.");
            return;
        }
        StatusText.Text = RecoveredReplyStatus;
        Trace.TraceInformation(string.Create(CultureInfo.InvariantCulture,
            $"Boo voice: recovery speech handed to the player (bytes={audio.Length}, speechSeconds={speech:0.00})."));
    }

    private void ReportRecoveryUnavailable(Exception ex, RealtimeClient source, long generation, long turn, CancellationToken cancellation)
    {
        Trace.TraceWarning("Boo voice: the silent-reply recovery could not get speech (" + ex.GetType().Name + ").");
        if (RecoveryIsCurrent(source, generation, turn, cancellation))
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
    /// The one way reply audio reaches the speaker: each realtime audio delta and any recovered speech. Returns false when
    /// the player failed; then the text stays visible and the status line says so.
    /// </summary>
    private bool PlayReplyAudio(byte[] audio)
    {
        try
        {
            if (_playReplyAudio is { } play)
            {
                play(audio);
            }
            else
            {
                (_audio ??= CreateAudio()).Play(audio);
            }
            return true;
        }
        catch (InvalidOperationException ex)
        {
            StatusText.Text = SanitizedError(ex, RecoveryUnavailableStatus);
            return false;
        }
    }
}
