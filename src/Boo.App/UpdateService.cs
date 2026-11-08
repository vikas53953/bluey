using Boo.Core;
using Velopack;
using Velopack.Sources;

namespace Boo.App;

/// <summary>
/// Thin wrapper over Velopack's <see cref="UpdateManager"/> pointed at the pinned private GitHub repository
/// (<see cref="UpdateChannel.RepositoryUrl"/>). Every decision about whether to check, and whether a found
/// release may replace the running build, is made in Boo.Core (<see cref="UpdatePolicy"/>,
/// <see cref="UpdateVersionGate"/>); this class only talks to GitHub and to the installer. Velopack checks the
/// SHA of every downloaded package against the release feed before it is applied.
/// </summary>
internal sealed class UpdateService
{
    private readonly UpdateManager _manager;
    private readonly bool _includePrerelease;
    private UpdateInfo? _pending;

    public UpdateService(string? accessToken, bool includePrerelease)
    {
        if (!UpdateChannel.IsTrustedRepositoryUrl(UpdateChannel.RepositoryUrl))
            throw new InvalidOperationException("The update repository is not a trusted GitHub URL.");
        _includePrerelease = includePrerelease;
        _manager = new UpdateManager(new GithubSource(UpdateChannel.RepositoryUrl, accessToken, includePrerelease));
    }

    /// <summary>Installed by Boo-win-Setup.exe (updatable) or a portable/dev copy (not updatable).</summary>
    public UpdateInstallKind InstallKind => _manager.IsInstalled ? UpdateInstallKind.Installed : UpdateInstallKind.Portable;

    public string CurrentVersion =>
        (_manager.IsInstalled ? _manager.CurrentVersion?.ToString() : null)
        ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3)
        ?? "unknown";

    /// <summary>Version string of a release that has been found and downloaded, ready to apply.</summary>
    public string? DownloadedVersion { get; private set; }

    /// <summary>
    /// Asks GitHub Releases for the newest release. Returns its version when it is a genuine upgrade for this
    /// build (and a pre-release only when the user opted in), otherwise null.
    /// </summary>
    public async Task<string?> CheckAsync(CancellationToken cancellationToken)
    {
        _pending = null;
        DownloadedVersion = null;
        var info = await _manager.CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(true);
        if (info is null)
            return null;
        var candidate = info.TargetFullRelease.Version.ToString();
        if (!UpdateVersionGate.IsUpgrade(CurrentVersion, candidate, _includePrerelease))
            return null;
        _pending = info;
        return candidate;
    }

    /// <summary>Downloads the release found by <see cref="CheckAsync"/>; Velopack verifies its hash.</summary>
    public async Task<string> DownloadAsync(IProgress<int> progress, CancellationToken cancellationToken)
    {
        var info = _pending ?? throw new InvalidOperationException("Check for updates first.");
        await _manager.DownloadUpdatesAsync(info, progress.Report, cancelToken: cancellationToken).ConfigureAwait(true);
        DownloadedVersion = info.TargetFullRelease.Version.ToString();
        return DownloadedVersion;
    }

    /// <summary>Hands over to Velopack: Boo exits, the update is applied, Boo restarts.</summary>
    public void ApplyAndRestart()
    {
        var info = _pending ?? throw new InvalidOperationException("Check for updates first.");
        if (DownloadedVersion is null)
            throw new InvalidOperationException("Download the update first.");
        _manager.ApplyUpdatesAndRestart(info);
    }
}
