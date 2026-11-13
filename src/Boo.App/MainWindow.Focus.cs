using System.Windows;
using Boo.Core;

namespace Boo.App;

/// <summary>
/// Focus session glue (plan unit U6): the session clock, check scheduling, nudges and the end event live in
/// <see cref="FocusSessionController"/>; the history bars come from <see cref="FocusTimeline"/>.
/// This file only reacts to them.
/// </summary>
public partial class MainWindow
{
    /// <summary>Width of the history timeline bar; must match the bar Border in MainWindow.xaml.</summary>
    private const double HistoryBarWidth = 260;

    /// <summary>
    /// Raised once when a focus session ends (completed or ended by the user), after it is saved.
    /// The app forwards it to the notch so it can show the happy face.
    /// </summary>
    public event EventHandler<FocusSessionEndedEventArgs>? FocusSessionEnded;

    private void Focus_OnSessionEnded(object? sender, FocusSessionEndedEventArgs e)
    {
        if (!ReferenceEquals(sender, _focus))
        {
            return;
        }

        try
        {
            _focusTimer.Stop();
            StopFocusMonitoring();
            SaveFocusSnapshot();
            UpdateFocusView();
            FocusStatus.Text = e.Reason == FocusEndReason.Completed
                ? "Session complete. Nice work. It is saved in your history."
                : "Ended. Start a new session explicitly.";
            _chirps.PlaySessionEnd(_settings.ChirpsEnabled, _listening);
        }
        finally
        {
            // The end event is raised only once, so the notch must hear about it even if a chirp fails.
            SetFace(_faceInputs with { FocusPaused = false, Happy = true }, TimeSpan.FromSeconds(2.5), FaceMomentKind.Happy);
            FocusSessionEnded?.Invoke(this, e);
        }
    }

    private void DeveloperModeBox_OnChanged(object sender, RoutedEventArgs e)
    {
        var enabled = DeveloperModeBox.IsChecked == true;
        if (_focus is { Status: not Boo.Core.FocusStatus.Ended })
        {
            _focus.SetDeveloperMode(enabled);
            UpdateFocusView();
        }

        DeveloperModeStatus.Text = enabled
            ? "Developer Mode on: the focus clock runs 4x and checks happen every 10 seconds."
            : "Developer Mode off: real-time clock, checks every 30 seconds.";
    }

    private void FocusStartButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FocusTaskBox.Text))
        {
            FocusStatus.Text = "Name the focus task first.";
            return;
        }
        if (!double.TryParse(FocusDurationBox.Text, out var minutes) || minutes <= 0)
        {
            FocusStatus.Text = "Enter a positive duration in minutes.";
            return;
        }

        try
        {
            _focusCancellation.Cancel();
            _focusCancellation.Dispose();
            _focusCancellation = new CancellationTokenSource();
            if (_focus is { Status: not Boo.Core.FocusStatus.Ended } superseded)
            {
                // Start while a session is live replaces it explicitly: settle it as ended and save it, so it is not
                // left Running in sessions.db and reported as "interrupted when Boo closed" at the next start.
                // No happy face or chirp: those belong to the session the user finishes.
                superseded.SessionEnded -= Focus_OnSessionEnded;
                superseded.EndQuietly();
                SaveFocusSnapshot(superseded);
            }
            _focus = new FocusSessionController(FocusTaskBox.Text.Trim(), Math.Clamp(minutes * 60d, 60d, 14400d),
                DeveloperModeBox.IsChecked == true, _timeProvider);
            _focus.SessionEnded += Focus_OnSessionEnded;
            _focusCaptureInFlight = false;
            _lastFocusSaveRealElapsed = 0;
            _focusClock.Start();
            _focusTimer.Start();
            _focusCaptureTimer.Start();
            SaveFocusSnapshot();
            UpdateFocusView();
            FocusStatus.Text = "Running; monitoring pauses on Stop, lock, sleep, or Pause.";
            SetFace(_faceInputs with { FocusPaused = false, Happy = false });
        }
        catch (Exception ex)
        {
            FocusStatus.Text = SanitizedError(ex, "Focus session is not available yet.");
        }
    }

    /// <summary>
    /// Pauses a RUNNING session only. On an ended (or already paused) session it does nothing: it used to claim
    /// "Paused" and pin the notch on the Paused face with nothing to resume or end (issue #70).
    /// </summary>
    private void FocusPauseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_focus is not { Status: Boo.Core.FocusStatus.Running })
        {
            return;
        }
        try
        {
            _focus.Pause("User pause");
            _focusTimer.Stop();
            StopFocusMonitoring();
            SaveFocusSnapshot();
            UpdateFocusView();
            FocusStatus.Text = "Paused. Resume is explicit; unlock/reconnect will not resume it.";
            SetFace(WithFocusFace(_faceInputs));
        }
        catch (Exception ex)
        {
            FocusStatus.Text = SanitizedError(ex, "Could not pause focus.");
        }
    }

    /// <summary>Nudge pills on the notch: "Back to it" dismisses the nudge; "5 min break" pauses focus (resume stays explicit).</summary>
    public void HandleNudgeAction(bool takeBreak)
    {
        SetFace(_faceInputs with { NudgeShowing = false });
        if (takeBreak && _focus is { Status: Boo.Core.FocusStatus.Running })
        {
            FocusPauseButton_OnClick(this, new RoutedEventArgs());
            FocusStatus.Text = "On a 5 minute break. Press Resume when you are back.";
        }
    }

    private void FocusResumeButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_focus is null)
        {
            return;
        }
        try
        {
            if (_focus.Status != Boo.Core.FocusStatus.Paused)
            {
                return;
            }
            _focus.Resume();
            _focusCancellation.Dispose();
            _focusCancellation = new CancellationTokenSource();
            _focusCaptureTimer.Start();
            _focusClock.Start();
            _focusTimer.Start();
            UpdateFocusView();
            FocusStatus.Text = "Running after explicit Resume.";
            SetFace(WithFocusFace(_faceInputs));
        }
        catch (Exception ex)
        {
            FocusStatus.Text = SanitizedError(ex, "Could not resume focus.");
        }
    }

    private void FocusEndButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_focus is null)
        {
            return;
        }
        try
        {
            _focus.End(); // Focus_OnSessionEnded stops, saves and updates the view.
        }
        catch (Exception ex)
        {
            FocusStatus.Text = SanitizedError(ex, "Could not end focus.");
        }
    }

    /// <summary>
    /// The notch's Paused face always follows the session's REAL status, never the button or event that asked for a
    /// pause: a pause request on a session that is not running (ended, or already paused) changes nothing (issue #70).
    /// </summary>
    private FaceInputs WithFocusFace(FaceInputs inputs) =>
        inputs with { FocusPaused = _focus is { Status: Boo.Core.FocusStatus.Paused } };
}
