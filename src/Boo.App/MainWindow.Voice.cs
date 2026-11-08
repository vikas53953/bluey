using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Boo.Core;
using Boo.Providers;

namespace Boo.App;

/// <summary>
/// Voice focus flow (plan unit U8, Riley's "hold the face, say what you are working on"): the Realtime session's
/// focus_start / focus_pause / focus_resume / focus_end / focus_status / go_to_sleep tools. Parsing and the reply
/// policy live in <see cref="VoiceCommandParser"/> and <see cref="VoiceFocusPolicy"/>; this file applies a decision
/// through the same code paths as the focus buttons.
/// </summary>
public partial class MainWindow
{
    /// <summary>Longest wait for the spoken "Going quiet." before the connection is ended anyway.</summary>
    private static readonly TimeSpan SleepFallback = TimeSpan.FromSeconds(8);

    private RealtimeClient? _sleepSource;
    private bool _sleepSawAudio;
    private DispatcherTimer? _sleepTimer;

    private void AttachVoiceCommands(RealtimeClient realtime)
    {
        realtime.VoiceCommandHandler = (command, cancellationToken) =>
            Dispatcher.InvokeAsync(() => ApplyVoiceCommand(realtime, command), DispatcherPriority.Normal, cancellationToken).Task;
    }

    private string ApplyVoiceCommand(RealtimeClient source, VoiceCommand command)
    {
        if (!ReferenceEquals(_realtime, source))
        {
            return RealtimeClient.VoiceCommandsUnavailableText;
        }

        var decision = VoiceFocusPolicy.Decide(command, _focus?.Snapshot, _focus?.Remaining ?? 0);
        if (!decision.Apply)
        {
            return decision.Reply;
        }

        switch (command.Kind)
        {
            case VoiceCommandKind.FocusStart:
                FocusTaskBox.Text = command.Task ?? string.Empty;
                // Whole minutes (the reply says the same); an integer parses identically in every culture.
                FocusDurationBox.Text = ((int)Math.Round(command.Minutes)).ToString(CultureInfo.InvariantCulture);
                FocusStartButton_OnClick(this, new RoutedEventArgs());
                if (_focus is not { Status: Boo.Core.FocusStatus.Running })
                {
                    return "Boo could not start focus.";
                }
                break;
            case VoiceCommandKind.FocusPause:
                FocusPauseButton_OnClick(this, new RoutedEventArgs());
                break;
            case VoiceCommandKind.FocusResume:
                FocusResumeButton_OnClick(this, new RoutedEventArgs());
                break;
            case VoiceCommandKind.FocusEnd:
                FocusEndButton_OnClick(this, new RoutedEventArgs());
                break;
            case VoiceCommandKind.GoToSleep:
                ArmSleepAfterReply(source);
                break;
        }

        return decision.Reply;
    }

    /// <summary>
    /// go_to_sleep: the tool output is still to be sent and the model still has to say "Going quiet.", so the voice
    /// session ends once that reply has finished playing (or after <see cref="SleepFallback"/>). Focus is untouched.
    /// </summary>
    private void ArmSleepAfterReply(RealtimeClient source)
    {
        _sleepSource = source;
        _sleepSawAudio = false;
        ScheduleSleep(SleepFallback);
    }

    /// <summary>Called for every accepted Realtime event, next to <see cref="ObserveTalkEvent"/>.</summary>
    private void ObserveSleepAfterReply(RealtimeClient source, RealtimeEvent realtimeEvent)
    {
        if (_sleepSource is null || !ReferenceEquals(_sleepSource, source))
        {
            return;
        }

        switch (realtimeEvent.Kind)
        {
            case "response.output_audio.delta":
                _sleepSawAudio = true;
                break;
            case "response.done" when _sleepSawAudio:
                // The spoken reply is complete; let the queued audio finish, then go quiet.
                var remaining = TimeSpan.FromMilliseconds(Math.Max(0, Interlocked.Read(ref _playbackUntilMs) - Environment.TickCount64));
                ScheduleSleep(remaining + FaceMoments.PlaybackSlack);
                break;
        }
    }

    private void ScheduleSleep(TimeSpan delay)
    {
        if (_sleepTimer is null)
        {
            _sleepTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
            _sleepTimer.Tick += (_, _) => GoToSleepNow();
        }
        _sleepTimer.Stop();
        _sleepTimer.Interval = delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(1);
        _sleepTimer.Start();
    }

    private void GoToSleepNow()
    {
        _sleepTimer?.Stop();
        var source = _sleepSource;
        _sleepSource = null;
        _sleepSawAudio = false;
        if (source is null || !ReferenceEquals(_realtime, source))
        {
            return; // Stop, an error, or a new session already ended this one.
        }

        // Voice only: the same teardown as a dropped connection, without pausing focus or running StopAll.
        AbortHoldToTalk();
        StopListening();
        ListeningStatus.Text = "Boo went quiet. Activate Talk or hold Boo's eyes to speak again.";
    }
}
