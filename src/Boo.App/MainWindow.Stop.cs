using Boo.Core;

namespace Boo.App;

/// <summary>
/// Stop (Ctrl+Alt+F12, Ctrl+Alt+S, the Stop button, the tray, a right-click on the notch): one <see cref="StopAll"/>
/// that ends every request, voice turn, overlay and pending action, pauses a running focus session, and leaves no
/// "Boo is working" status line behind (issue #71).
/// </summary>
public partial class MainWindow
{
    private const string StoppedStatusMessage = "Stopped. Nothing is running now; ask again when you are ready.";
    // The "Boo is working" text last put in the status line (ShowBusyStatus), so Stop can replace it (issue #71).
    private string? _busyStatusText;

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
        // A cancelled request no longer writes its own message (it is not current any more), so Stop must not leave a
        // "Boo is working" line behind. Replace it only while it is still showing: an error or a newer message stays.
        if (_busyStatusText is not null && StatusText.Text == _busyStatusText)
        {
            StatusText.Text = StoppedStatusMessage;
        }
        _busyStatusText = null;
        UpdateCaptureIndicator();
        SetFace(new FaceInputs(FocusPaused: _focus is { Snapshot.Status: Boo.Core.FocusStatus.Paused }));
    }

    /// <summary>
    /// Every status line that says Boo is still working goes through here, so <see cref="StopAll"/> can tell it is
    /// stale and replace it (issue #71: after Stop the line kept saying "Thinking about the selected context…").
    /// Writes nothing once the request it belongs to is no longer current: a continuation that resumes after Stop (or
    /// after a newer request) must not put a "working" line back.
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
