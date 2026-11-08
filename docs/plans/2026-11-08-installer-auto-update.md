# Boo installer and opt-in auto-update (8 Nov 2026)

Branch `claude/project-thread-e9urgv` off `lane/face`. Goal: a per-user Windows installer with a Start menu
entry and uninstall, plus opt-in updates from GitHub Releases of the private repo `vikas53953/bluey`,
built in CI as an artifact next to the existing portable ZIP.

## Decision: Velopack, not MSIX

| | Velopack | MSIX |
|---|---|---|
| Works unsigned | Yes (SmartScreen "unknown publisher" only) | No: needs a trusted code-signing cert or sideload cert on every PC |
| Per-user install, Start menu, Add/Remove Programs | Yes, `%LocalAppData%\Boo` | Yes |
| Updates from a private GitHub repo | Built in (`GithubSource` + token), delta packages | Needs an App Installer feed on an HTTPS host; no private-GitHub support |
| .NET 10 WPF self-contained | Yes, packs the publish folder as is | Yes |

MSIX without a paid certificate would make every tester import a certificate as a trusted root, so Velopack wins.

## Pieces

1. **Boo.Core (pure, tested)**: `UpdateSettings`, `UpdateChannel`, `UpdatePolicy`, `UpdateVersionGate`,
   `GitHubAccessToken`, `UpdateStatusText`. No Velopack dependency; everything is decided from values.
2. **Boo.App**: `Program.Main` runs `VelopackApp.Build().Run()` before WPF starts (install/uninstall/update hooks).
   `UpdateService` wraps Velopack's `UpdateManager` + `GithubSource`. Settings > Updates: opt-in checkbox,
   pre-release checkbox, GitHub token (DPAPI-protected, file `update-token.bin`, never shown again),
   **Check for updates** and **Install and restart** buttons, status line.
3. **CI**: `release.yml` runs `vpk pack` over the self-contained publish folder, uploads
   `boo-installer-win-x64` (Boo-win-Setup.exe, full .nupkg, releases.win.json, portable zip). A `v*` tag
   attaches those files to the GitHub Release so `GithubSource` can find them.

## Core API contract (what the tests cover)

```csharp
namespace Boo.Core;

// Added to AppSettings as the last positional parameter: `UpdateSettings? Updates = null`.
public sealed record UpdateSettings(bool Enabled = false, bool IncludePrerelease = false, DateTimeOffset? LastCheckedUtc = null);

public static class UpdateChannel
{
    public const string RepositoryUrl = "https://github.com/vikas53953/bluey"; // pinned; not user-editable
    public const string PackId = "Boo";
    public const string ChannelName = "win";
    // True only for https://github.com/<owner>/<repo> (optional trailing slash or .git). http, other hosts,
    // user-info, query strings, extra path segments and whitespace are rejected.
    public static bool IsTrustedRepositoryUrl(string? url);
}

public enum UpdateInstallKind { Installed, Portable }

public enum UpdateCheckDecision { Check, DisabledByUser, PortableBuild, MissingToken, CheckedRecently }

public static class UpdatePolicy
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    public static UpdateSettings Effective(AppSettings settings); // settings.Updates ?? new UpdateSettings()
    // Order of precedence: DisabledByUser > PortableBuild > MissingToken > CheckedRecently > Check.
    // CheckedRecently when now - LastCheckedUtc < CheckInterval (a LastCheckedUtc in the future also counts as recent).
    public static UpdateCheckDecision DecideStartupCheck(UpdateSettings settings, UpdateInstallKind install, bool hasToken, DateTimeOffset now);
    // Manual "Check for updates" ignores the interval and the Enabled flag but still needs an installed build and a token.
    public static UpdateCheckDecision DecideManualCheck(UpdateInstallKind install, bool hasToken);
}

public static class UpdateVersionGate
{
    // SemVer 2 compare. Candidate must be strictly newer than current; a pre-release candidate (has "-") is
    // only accepted when includePrerelease is true. Leading "v" is tolerated on either side. Unparseable → false.
    public static bool IsUpgrade(string current, string candidate, bool includePrerelease);
}

public static class GitHubAccessToken
{
    // null when acceptable. Rejects null/empty/whitespace, embedded whitespace or control characters,
    // tokens shorter than 20 characters, and tokens that do not start with "ghp_", "github_pat_" or "gho_".
    // Trims surrounding whitespace before checking.
    public static string? RejectReason(string? token);
    public static string Normalize(string token); // trimmed
    // Never reveals the token: "ghp_…" + last 4 characters, e.g. "ghp_…WXYZ"; for "github_pat_" prefix likewise.
    public static string Mask(string token);
}

public enum UpdateCheckOutcome { NotChecked, Disabled, PortableBuild, MissingToken, Checking, UpToDate, UpdateAvailable, Downloading, ReadyToRestart, Failed }

public static class UpdateStatusText
{
    // Human text for the Settings status line. Must never include a token. Examples:
    // NotChecked      → "Updates have not been checked yet."
    // Disabled        → "Automatic update checks are off. Boo never contacts GitHub unless you turn them on."
    // PortableBuild   → "This is a portable build. Updates apply to the installed version only (run Boo-win-Setup.exe)."
    // MissingToken    → "Add a GitHub token with read access to the Boo repository to check for updates."
    // Checking        → "Checking GitHub Releases…"
    // UpToDate        → "Boo <current> is up to date." (uses current)
    // UpdateAvailable → "Boo <available> is available (you have <current>). Press Install and restart."
    // Downloading     → "Downloading Boo <available>… <percent>%"
    // ReadyToRestart  → "Boo <available> is downloaded. Press Install and restart to finish."
    // Failed          → "Update check failed: <error>" where error is sanitised with SanitizeError (below)
    public static string Describe(UpdateCheckOutcome outcome, string? current = null, string? available = null, int percent = 0, string? error = null);
    // Removes anything that looks like a token (ghp_/github_pat_/gho_ followed by [A-Za-z0-9_]+) and
    // collapses it to "[token]"; also strips newlines. Null/empty → "unknown error".
    public static string SanitizeError(string? error);
}
```
