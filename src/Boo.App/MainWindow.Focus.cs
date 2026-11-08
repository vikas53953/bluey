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
            SetFace(_faceInputs with { FocusPaused = false, Happy = true }, TimeSpan.FromSeconds(2.5));
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
}
