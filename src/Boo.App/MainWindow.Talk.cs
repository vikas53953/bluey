using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;
using Boo.Core;
using Boo.Providers;
using Boo.Windows;
using Microsoft.Win32;

namespace Boo.App;

/// <summary>
/// Hold-to-talk glue (plan unit U4): Ctrl+Alt+Space via a low-level keyboard hook, or holding Boo's eyes,
/// drives <see cref="HoldToTalkController"/>; its commands run a push-to-talk Realtime turn (server VAD off,
/// commit + response.create on release). Logic lives in Boo.Core; this file only carries commands out.
/// </summary>
public partial class MainWindow
{
    /// <summary>The Realtime API rejects commits shorter than 100 ms (24 kHz mono PCM16 = 4,800 bytes).</summary>
    private const long MinimumCommitBytes = 4_800;
    private const double PlaybackBytesPerMs = 48; // 24 kHz * 2 bytes

    private readonly object _talkGate = new();
    private readonly LatencyRecorder _latency = new();
    private HoldToTalkController? _talk;
    private LowLevelKeyboardHook? _talkHook;
    private DispatcherTimer? _talkTimer;
    private PushToTalkUplink? _uplink;
    private AudioSession? _talkAudio;
    private bool _realtimePushToTalk;
    private bool _responseActive;
    private long _playbackUntilMs;
    private DateTimeOffset? _endOfSpeech;

    /// <summary>Boo's eyes pressed (true) or released (false) with the mouse. Called by the notch.</summary>
    public void SetEyesHeld(bool held) => RunTalk(c =>
    {
        if (held)
        {
            c.SetPlaybackActive(IsPlaybackActive());
            return c.EyesPressed();
        }

        return c.EyesReleased();
    });

    /// <summary>The window holding the eyes lost focus or mouse capture; ends an eye hold.</summary>
    public void TalkFocusLost() => RunTalk(c => c.FocusLost());

    private void InitializeHoldToTalk()
    {
        if (_talk is not null)
        {
            return;
        }

        _talk = new HoldToTalkController(TimeProvider.System);
        _talkTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _talkTimer.Tick += (_, _) => RunTalk(c => c.Tick());
        SystemEvents.SessionSwitch += Talk_OnSessionSwitch;
        SystemEvents.PowerModeChanged += Talk_OnPowerModeChanged;
        try
        {
            _talkHook = new LowLevelKeyboardHook(OnTalkKey);
            _talkHook.Start();
        }
        catch (Win32Exception)
        {
            _talkHook?.Dispose();
            _talkHook = null;
            ListeningStatus.Text = "Ctrl+Alt+Space is unavailable; use Talk or hold Boo's eyes.";
        }
        UpdateLatencyText();
    }

    private void DisposeHoldToTalk()
    {
        SystemEvents.SessionSwitch -= Talk_OnSessionSwitch;
        SystemEvents.PowerModeChanged -= Talk_OnPowerModeChanged;
        _talkTimer?.Stop();
        _talkHook?.Dispose();
        _talkHook = null;
        DiscardPushToTalkUplink();
    }

    /// <summary>Called from <see cref="StopAll"/> (Ctrl+Alt+F12, tray Stop).</summary>
    private void StopHoldToTalk() => RunTalk(c => c.Stop());

    // Runs on the hook thread: must stay well under 1 ms and never wait on the UI thread.
    private bool OnTalkKey(TalkKey key, bool down, bool isRepeat)
    {
        TalkStep step;
        lock (_talkGate)
        {
            var controller = _talk;
            if (controller is null)
            {
                return false;
            }
            if (down && !isRepeat)
            {
                controller.SetPlaybackActive(IsPlaybackActive());
            }
            step = down ? controller.KeyDown(key, isRepeat) : controller.KeyUp(key);
        }

        if (step.Commands.Count > 0 || step.Hint is not null)
        {
            PostTalkStep(step);
        }
        return step.SwallowKey;
    }

    private void Talk_OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff)
        {
            PostTalk(c => c.SessionLocked());
        }
    }

    private void Talk_OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            PostTalk(c => c.Suspending());
        }
    }

    private void PostTalk(Func<HoldToTalkController, TalkStep> input)
    {
        TalkStep step;
        lock (_talkGate)
        {
            if (_talk is null)
            {
                return;
            }
            step = input(_talk);
        }
        PostTalkStep(step);
    }

    private void PostTalkStep(TalkStep step)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }
        try
        {
            Dispatcher.BeginInvoke(() => ExecuteTalkStep(step));
        }
        catch (InvalidOperationException) { /* dispatcher shutting down */ }
    }

    private void RunTalk(Func<HoldToTalkController, TalkStep> input)
    {
        TalkStep step;
        lock (_talkGate)
        {
            if (_talk is null)
            {
                return;
            }
            step = input(_talk);
        }
        ExecuteTalkStep(step);
    }

    private void ExecuteTalkStep(TalkStep step)
    {
        foreach (var command in step.Commands)
        {
            switch (command)
            {
                case TalkCommand.Interrupt:
                    InterruptPlayback();
                    break;
                case TalkCommand.StartListening:
                    StartPushToTalk();
                    break;
                case TalkCommand.StopAndSend:
                    _ = SendPushToTalkAsync();
                    break;
                case TalkCommand.Cancel:
                    CancelPushToTalk();
                    break;
            }
        }

        if (step.Hint is not null)
        {
            ListeningStatus.Text = step.Hint;
        }

        bool listening;
        lock (_talkGate)
        {
            listening = _talk?.IsListening == true;
        }
        if (listening)
        {
            _talkTimer?.Start();
        }
        else
        {
            _talkTimer?.Stop();
        }
    }

    private bool IsPlaybackActive() => Environment.TickCount64 < Interlocked.Read(ref _playbackUntilMs);

    private void InterruptPlayback()
    {
        Interlocked.Exchange(ref _playbackUntilMs, 0);
        _endOfSpeech = null;
        _audio?.ClearPlayback();
        if (_responseActive && _realtimeConnected && _realtime is { } realtime)
        {
            _responseActive = false;
            _ = InterruptRealtimeAsync(realtime);
        }
    }

    private async void StartPushToTalk()
    {
        PushToTalkUplink? uplink = null;
        var lease = _requestLease;
        try
        {
            var realtime = _realtime;
            var reuse = _realtimeConnected && _realtimePushToTalk && realtime is not null
                && lease is not null && IsCurrent(lease.Generation) && !lease.Token.IsCancellationRequested;
            if (!reuse)
            {
                lease = BeginRequest();
                realtime = EnsureRealtime(pushToTalk: true);
            }

            uplink = new PushToTalkUplink();
            _ = Interlocked.Exchange(ref _uplink, uplink)?.Discard();

            // Open the microphone first so nothing said while connecting is lost; frames buffer in the uplink.
            if (_audio is null || !ReferenceEquals(_audio, _talkAudio))
            {
                _audio?.Dispose();
                var audio = CreateAudio();
                audio.Captured += pcm => Volatile.Read(ref _uplink)?.Append(pcm);
                _audio = audio;
                _talkAudio = audio;
            }
            _audio.Start(_settings.MicrophoneDevice);
            _listening = true;
            _onMicrophoneActivity(true);
            ListeningStatus.Text = "Listening… release to send.";

            if (!_realtimeConnected)
            {
                await realtime!.ConnectAsync(lease!.Token);
                if (!IsCurrent(lease.Generation) || !ReferenceEquals(_realtime, realtime))
                {
                    _ = uplink.Discard();
                    return;
                }
                _realtimeConnected = true;
                _voiceTaskAuthorized = false;
                _latency.Connected();
            }
            uplink.Attach(realtime!.SendAudioAsync);
        }
        catch (Exception) when (uplink is not null && ((lease is not null && !IsCurrent(lease.Generation))
            || (Volatile.Read(ref _uplink) is { } current && !ReferenceEquals(current, uplink))))
        {
            // A newer hold superseded this one while it was connecting (its BeginRequest cancelled our lease).
            // Only drop this turn's audio; the newer turn owns the microphone and the current uplink.
            _ = uplink.Discard();
        }
        catch (OperationCanceledException)
        {
            _ = uplink?.Discard(); // a release may already own it and be waiting for Attach
            DiscardPushToTalkUplink();
            StopPushToTalkMic();
            ListeningStatus.Text = "Listening stopped.";
        }
        catch (Exception ex)
        {
            _ = uplink?.Discard();
            StopListening();
            ListeningStatus.Text = "Microphone unavailable. You can still type a request.";
            StatusText.Text = SanitizedError(ex, "Microphone or realtime setup failed.");
        }
    }

    private async Task SendPushToTalkAsync()
    {
        var endOfSpeech = DateTimeOffset.UtcNow;
        var uplink = Interlocked.Exchange(ref _uplink, null);
        StopPushToTalkMic();
        var realtime = _realtime;
        var lease = _requestLease;
        if (uplink is null || realtime is null || lease is null)
        {
            _ = uplink?.Discard();
            return;
        }

        ListeningStatus.Text = "Thinking…";
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lease.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var bytes = await uplink.CompleteAsync(deadline.Token);
            if (!IsCurrent(lease.Generation) || !_realtimeConnected || !ReferenceEquals(_realtime, realtime))
            {
                return;
            }
            if (bytes < MinimumCommitBytes)
            {
                await realtime.ClearAudioBufferAsync(CancellationToken.None);
                ListeningStatus.Text = HoldToTalkController.TapHint;
                return;
            }
            _endOfSpeech = endOfSpeech;
            await realtime.CommitAudioTurnAsync(CancellationToken.None);
            StatusText.Text = "Voice turn sent; waiting for the reply…";
        }
        catch (OperationCanceledException)
        {
            _ = uplink.Discard();
            _endOfSpeech = null;
            if (IsCurrent(lease.Generation))
            {
                ListeningStatus.Text = "Voice turn was not sent. Hold to talk to retry.";
            }
        }
        catch (Exception ex)
        {
            _endOfSpeech = null;
            if (IsCurrent(lease.Generation))
            {
                StopListening();
                StatusText.Text = SanitizedError(ex, "The voice turn could not be sent; typing remains available.");
            }
        }
    }

    private void CancelPushToTalk()
    {
        var uplink = Interlocked.Exchange(ref _uplink, null);
        StopPushToTalkMic();
        ListeningStatus.Text = "Listening cancelled.";
        if (uplink is null)
        {
            return;
        }

        var attached = uplink.IsAttached;
        var drained = uplink.Discard();
        if (attached && _realtimeConnected && _realtime is { } realtime)
        {
            _ = ClearAfterAsync(drained, realtime);
        }
    }

    private static async Task ClearAfterAsync(Task drained, RealtimeClient realtime)
    {
        try
        {
            await drained.ConfigureAwait(false);
            await realtime.ClearAudioBufferAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The connection may already be closed; nothing was committed either way.
        }
    }

    private void StopPushToTalkMic()
    {
        if (_talkAudio is not null && ReferenceEquals(_audio, _talkAudio))
        {
            try
            {
                _talkAudio.Stop();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                StatusText.Text = SanitizedError(ex, "Microphone stopped");
            }
        }
        _listening = false;
        _onMicrophoneActivity(false);
    }

    /// <summary>Called from <see cref="StopListening"/>: drop any in-progress push-to-talk audio.</summary>
    private void DiscardPushToTalkUplink()
    {
        _ = Interlocked.Exchange(ref _uplink, null)?.Discard();
        _endOfSpeech = null;
        _responseActive = false;
        Interlocked.Exchange(ref _playbackUntilMs, 0);
    }

    /// <summary>Called for every accepted Realtime event before the main switch.</summary>
    private void ObserveTalkEvent(RealtimeEvent realtimeEvent)
    {
        switch (realtimeEvent.Kind)
        {
            case "response.created":
                _responseActive = true;
                break;
            case "response.done":
                _responseActive = false;
                break;
            case "response.output_audio.delta" when realtimeEvent.Audio is { Length: > 0 } audio:
                if (SpeechMute.IsChecked != true)
                {
                    var now = Environment.TickCount64;
                    var start = Math.Max(now, Interlocked.Read(ref _playbackUntilMs));
                    Interlocked.Exchange(ref _playbackUntilMs, start + (long)Math.Ceiling(audio.Length / PlaybackBytesPerMs));
                }
                if (_endOfSpeech is { } endOfSpeech)
                {
                    _endOfSpeech = null;
                    var firstAudio = DateTimeOffset.UtcNow;
                    if (firstAudio >= endOfSpeech)
                    {
                        _latency.Record(endOfSpeech, firstAudio);
                        UpdateLatencyText();
                    }
                }
                break;
        }
    }

    private void UpdateLatencyText()
    {
        var culture = CultureInfo.InvariantCulture;
        var median = _latency.WarmMedianMs();
        var p95 = _latency.WarmP95Ms();
        if (median is null || p95 is null)
        {
            VoiceLatencyText.Text = string.Format(culture,
                "Voice latency: no warm hold-to-talk turns yet ({0} cold).", _latency.ColdCount);
            return;
        }

        var turns = string.Join(", ", _latency.Samples.TakeLast(25).Select(s =>
            string.Format(culture, "{0}:{1:0}{2}", s.Turn, s.Latency.TotalMilliseconds, s.Cold ? "c" : string.Empty)));
        VoiceLatencyText.Text = string.Format(culture,
            "Voice latency (end of speech → first audio): warm {0}/{1}, median {2:0} ms, p95 {3:0} ms, {4}. Turns (c = cold): {5}",
            _latency.WarmCount, LatencyRecorder.TargetWarmTurns, median.Value, p95.Value,
            _latency.MeetsTarget() ? "meets target" : "target not met yet", turns);
    }
}
