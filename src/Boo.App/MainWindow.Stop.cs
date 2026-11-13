using Boo.Core;

namespace Boo.App;

/// <summary>Stop: the one action behind Ctrl+Alt+F12, Ctrl+Alt+S, the Stop button, the tray and the notch.</summary>
public partial class MainWindow
{
    private const string StoppedStatusMessage = "Stopped. Nothing is running now; ask again when you are ready.";
    /// <summary>The "Boo is working" text last put in the status line by <see cref="ShowBusyStatus"/>.</summary>
    private string? _busyStatusText;

    /// <summary>
    /// Ends every request, voice turn, overlay and pending action and pauses a running focus session. If the status line
    /// still shows a "working" message from <see cref="ShowBusyStatus"/>, it becomes <see cref="StoppedStatusMessage"/>;
    /// any other status text is left as it is.
    /// </summary>
    public void StopAll()
    {
        StopHoldToTalk();
        _generation++;
        _coordinator.Stop();

        _requestCancellation.Cancel();
        _requestCancellation.Dispose();
        _requestCancellation = new CancellationTokenSource();
        _requestLease = null;

        StopAudioUploads();

        try
        {
            _audio?.Stop();
        }
        catch (Exception ex) when (ex is InvalidOperationException)
        {
            StatusText.Text = SanitizedError(ex, "Audio stopped");
        }

        var realtimeToStop = _realtime;
        _realtime = null;
        if (realtimeToStop is not null)
        {
            _ = InterruptRealtimeAsync(realtimeToStop);
        }

        _listening = false;
        _realtimeConnected = false;
        _voiceTaskAuthorized = false;
        _onMicrophoneActivity(false);
        TalkButton.Content = "Talk";
        ListeningStatus.Text = "Microphone stopped. Screen activity is shown in the sharing indicator.";
        ClearAnnotations();
        ClearActionPreview();
        _ = CompletePendingRealtimeToolAsync("Action stopped before approval or commit.");
        if (_focus is { Snapshot.Status: Boo.Core.FocusStatus.Running })
        {
            _focus.Pause("Stopped");
            StopFocusMonitoring();
            SaveFocusSnapshot();
            _focusTimer.Stop();
            FocusStatus.Text = "Paused by Stop; Resume is explicit.";
        }
        if (_busyStatusText is not null && StatusText.Text == _busyStatusText)
        {
            StatusText.Text = StoppedStatusMessage;
        }
        _busyStatusText = null;
        UpdateCaptureIndicator();
        SetFace(new FaceInputs(FocusPaused: _focus is { Snapshot.Status: Boo.Core.FocusStatus.Paused }));
    }

    /// <summary>
    /// Shows a "working" status for the request of <paramref name="generation"/>, or does nothing if that request is no
    /// longer current. Every status that says Boo is still working must be set through this method.
    /// </summary>
    private void ShowBusyStatus(string text, long generation)
    {
        if (!IsCurrent(generation))
        {
            return;
        }
        _busyStatusText = text;
        StatusText.Text = text;
    }
}
