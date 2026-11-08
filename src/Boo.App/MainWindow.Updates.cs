using System.IO;
using System.Windows;
using Boo.Core;
using Boo.Storage;

namespace Boo.App;

/// <summary>
/// Settings &gt; Updates. Opt-in: nothing contacts GitHub until the user ticks "Check for updates" or presses the
/// button. The GitHub token (the repository is private) is protected with DPAPI in update-token.bin next to the
/// provider credential and is never shown again; the status line only ever shows <see cref="GitHubAccessToken.Mask"/>.
/// </summary>
public partial class MainWindow
{
    private CredentialStore? _updateTokenStore;
    private UpdateService? _updates;
    private CancellationTokenSource? _updateCancellation;
    private string? _availableVersion;
    private bool _updateBusy;

    private CredentialStore UpdateTokenStore =>
        _updateTokenStore ??= new CredentialStore(Path.Combine(_dataDirectory, "update-token.bin"));

    private string? LoadUpdateToken()
    {
        try
        {
            return UpdateTokenStore.Load();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Called from LoadSettings: reflects saved choices and runs the startup check only when the policy allows it.</summary>
    private void LoadUpdateSettings()
    {
        var updates = UpdatePolicy.Effective(_settings);
        UpdatesEnabledBox.IsChecked = updates.Enabled;
        UpdatesPrereleaseBox.IsChecked = updates.IncludePrerelease;
        var token = LoadUpdateToken();
        ShowUpdateTokenHint(token);
        _updates = CreateUpdateService(token, updates.IncludePrerelease);
        var install = _updates?.InstallKind ?? UpdateInstallKind.Portable;
        var decision = UpdatePolicy.DecideStartupCheck(updates, install, token is not null, DateTimeOffset.UtcNow);
        switch (decision)
        {
            case UpdateCheckDecision.Check:
                _ = RunUpdateCheckAsync();
                break;
            case UpdateCheckDecision.CheckedRecently:
                SetUpdateStatus(UpdateCheckOutcome.NotChecked);
                UpdateStatusLine.Text = $"Last checked {updates.LastCheckedUtc:yyyy-MM-dd HH:mm} UTC. Boo {_updates?.CurrentVersion} checks again after 24 hours.";
                break;
            default:
                SetUpdateStatus(OutcomeFor(decision));
                break;
        }
    }

    private UpdateService? CreateUpdateService(string? token, bool includePrerelease)
    {
        try
        {
            return new UpdateService(token, includePrerelease);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or TypeInitializationException)
        {
            return null;
        }
    }

    private void ShowUpdateTokenHint(string? token)
    {
        UpdateTokenHint.Text = token is null
            ? "No GitHub token saved. The Boo repository is private, so a fine-grained token with read access to its contents is needed."
            : $"GitHub token saved ({GitHubAccessToken.Mask(token)}). Paste a new one to replace it."
              + (GitHubAccessToken.ScopeNote(token) is { } scope ? " " + scope : string.Empty);
    }

    private static UpdateCheckOutcome OutcomeFor(UpdateCheckDecision decision) => decision switch
    {
        UpdateCheckDecision.DisabledByUser => UpdateCheckOutcome.Disabled,
        UpdateCheckDecision.PortableBuild => UpdateCheckOutcome.PortableBuild,
        UpdateCheckDecision.MissingToken => UpdateCheckOutcome.MissingToken,
        _ => UpdateCheckOutcome.NotChecked
    };

    private void SetUpdateStatus(UpdateCheckOutcome outcome, string? available = null, int percent = 0, string? error = null)
    {
        UpdateStatusLine.Text = UpdateStatusText.Describe(outcome, _updates?.CurrentVersion, available, percent, error);
        InstallUpdateButton.Visibility = outcome is UpdateCheckOutcome.UpdateAvailable or UpdateCheckOutcome.ReadyToRestart
            ? Visibility.Visible
            : Visibility.Collapsed;
        CheckUpdatesButton.IsEnabled = !_updateBusy;
        InstallUpdateButton.IsEnabled = !_updateBusy;
    }

    /// <summary>
    /// Builds the Updates part of AppSettings from the checkboxes, keeping the last-checked time. A pasted token
    /// is returned (not yet stored) so it is only written once the settings file itself saved; <c>Note</c> explains
    /// a rejected token (settings are still saved).
    /// </summary>
    private (UpdateSettings Settings, string? PendingToken, string? Note) ReadUpdateSettingsFromUi()
    {
        var previous = UpdatePolicy.Effective(_settings);
        var next = new UpdateSettings(
            Enabled: UpdatesEnabledBox.IsChecked == true,
            IncludePrerelease: UpdatesPrereleaseBox.IsChecked == true,
            LastCheckedUtc: previous.LastCheckedUtc);
        string? note = null;
        string? pending = null;
        if (UpdateTokenBox.SecurePassword.Length > 0)
        {
            var pasted = new System.Net.NetworkCredential(string.Empty, UpdateTokenBox.SecurePassword).Password;
            UpdateTokenBox.Clear();
            var reason = GitHubAccessToken.RejectReason(pasted);
            if (reason is null)
                pending = GitHubAccessToken.Normalize(pasted);
            else
                note = $"the GitHub token was not saved: {reason}";
        }
        return (next, pending, note);
    }

    private void SaveUpdateToken(string token) => UpdateTokenStore.Save(token);

    /// <summary>After settings are saved: rebuild the service with the current token and show the resulting state.</summary>
    private void RefreshUpdatesAfterSave()
    {
        if (_updateBusy)
        {
            // A check or download is running against the current service; new choices apply after it finishes.
            return;
        }
        var updates = UpdatePolicy.Effective(_settings);
        var token = LoadUpdateToken();
        ShowUpdateTokenHint(token);
        _updates = CreateUpdateService(token, updates.IncludePrerelease);
        _availableVersion = null;
        var install = _updates?.InstallKind ?? UpdateInstallKind.Portable;
        var decision = UpdatePolicy.DecideStartupCheck(updates, install, token is not null, DateTimeOffset.UtcNow);
        SetUpdateStatus(decision == UpdateCheckDecision.Check || decision == UpdateCheckDecision.CheckedRecently
            ? UpdateCheckOutcome.NotChecked
            : OutcomeFor(decision));
    }

    private async void CheckUpdatesButton_OnClick(object sender, RoutedEventArgs e)
    {
        var token = LoadUpdateToken();
        var install = _updates?.InstallKind ?? UpdateInstallKind.Portable;
        var decision = UpdatePolicy.DecideManualCheck(install, token is not null);
        if (decision != UpdateCheckDecision.Check)
        {
            SetUpdateStatus(OutcomeFor(decision));
            return;
        }
        await RunUpdateCheckAsync();
    }

    private async Task RunUpdateCheckAsync()
    {
        if (_updates is not { } service || _updateBusy)
            return;
        _updateBusy = true;
        _updateCancellation?.Cancel();
        _updateCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        SetUpdateStatus(UpdateCheckOutcome.Checking);
        try
        {
            _availableVersion = await service.CheckAsync(_updateCancellation.Token);
            RememberUpdateCheck(DateTimeOffset.UtcNow);
            _updateBusy = false;
            SetUpdateStatus(_availableVersion is null ? UpdateCheckOutcome.UpToDate : UpdateCheckOutcome.UpdateAvailable, _availableVersion);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _updateBusy = false;
            SetUpdateStatus(UpdateCheckOutcome.Failed, error: ex.Message);
        }
    }

    private void RememberUpdateCheck(DateTimeOffset now)
    {
        try
        {
            _settings = _settings with { Updates = UpdatePolicy.Effective(_settings) with { LastCheckedUtc = now } };
            (_settingsStore ??= new SettingsStore(Path.Combine(_dataDirectory, "settings.json"))).Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Not remembering the time only means one extra check next start.
        }
    }

    private async void InstallUpdateButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_updates is not { } service || _availableVersion is null || _updateBusy)
            return;
        _updateBusy = true;
        _updateCancellation?.Cancel();
        _updateCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try
        {
            if (service.DownloadedVersion is null)
            {
                var progress = new Progress<int>(p => SetUpdateStatus(UpdateCheckOutcome.Downloading, _availableVersion, p));
                SetUpdateStatus(UpdateCheckOutcome.Downloading, _availableVersion, 0);
                await service.DownloadAsync(progress, _updateCancellation.Token);
            }
            _updateBusy = false;
            SetUpdateStatus(UpdateCheckOutcome.ReadyToRestart, _availableVersion);
            // Stop capture, audio and pending work, tell Velopack to apply once this process exits, then shut
            // down normally so the tray icon and single-instance mutex are released before the new Boo starts.
            _onStop();
            service.ApplyAfterExit();
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _updateBusy = false;
            SetUpdateStatus(UpdateCheckOutcome.Failed, error: ex.Message);
        }
    }
}
