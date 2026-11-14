using System.Globalization;
using System.Text.RegularExpressions;

namespace Boo.Core;

/// <summary>
/// Opt-in update preferences (AppSettings.Updates, null until the user first saves them). Everything is off by
/// default: Boo never contacts GitHub unless <see cref="Enabled"/> is true or the user presses "Check for updates".
/// </summary>
public sealed record UpdateSettings(bool Enabled = false, bool IncludePrerelease = false, DateTimeOffset? LastCheckedUtc = null);

/// <summary>Where updates come from. The repository is pinned in code and never user-editable.</summary>
public static partial class UpdateChannel
{
    public const string RepositoryUrl = "https://github.com/vikas53953/bluey";
    public const string PackId = "Boo";
    public const string ChannelName = "win";

    /// <summary>
    /// True only for <c>https://github.com/&lt;owner&gt;/&lt;repo&gt;</c> (an optional trailing slash or ".git" is
    /// tolerated). Plain http, other hosts, user-info, query strings, fragments and extra path segments are rejected.
    /// </summary>
    public static bool IsTrustedRepositoryUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Trim().Length != url.Length)
            return false;
        return TrustedRepository().IsMatch(url);
    }

    [GeneratedRegex(@"^https://github\.com/[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9._-]+?(?:\.git)?/?$", RegexOptions.CultureInvariant)]
    private static partial Regex TrustedRepository();
}

public enum UpdateInstallKind
{
    /// <summary>Installed through Boo-win-Setup.exe; Velopack can replace it in place.</summary>
    Installed,
    /// <summary>Portable ZIP, CI output or a developer run; nothing can be updated in place.</summary>
    Portable
}

public enum UpdateCheckDecision
{
    Check,
    DisabledByUser,
    PortableBuild,
    MissingToken,
    CheckedRecently
}

/// <summary>Pure decisions about when an update check may run. The app supplies install kind, token presence and time.</summary>
public static class UpdatePolicy
{
    /// <summary>Minimum spacing between automatic (startup) checks.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    public static UpdateSettings Effective(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Updates ?? new UpdateSettings();
    }

    /// <summary>
    /// Startup check. Precedence: DisabledByUser, PortableBuild, MissingToken, CheckedRecently, Check.
    /// "Recent" means strictly less than <see cref="CheckInterval"/> has passed; a last-check time in the future
    /// (clock moved back) also counts as recent so a wrong clock cannot cause a burst of checks.
    /// </summary>
    public static UpdateCheckDecision DecideStartupCheck(UpdateSettings settings, UpdateInstallKind install, bool hasToken, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled)
            return UpdateCheckDecision.DisabledByUser;
        if (install == UpdateInstallKind.Portable)
            return UpdateCheckDecision.PortableBuild;
        if (!hasToken)
            return UpdateCheckDecision.MissingToken;
        if (settings.LastCheckedUtc is { } last && now - last < CheckInterval)
            return UpdateCheckDecision.CheckedRecently;
        return UpdateCheckDecision.Check;
    }

    /// <summary>A manual "Check for updates" ignores the Enabled flag and the interval but still needs an installed build and a token.</summary>
    public static UpdateCheckDecision DecideManualCheck(UpdateInstallKind install, bool hasToken)
    {
        if (install == UpdateInstallKind.Portable)
            return UpdateCheckDecision.PortableBuild;
        return hasToken ? UpdateCheckDecision.Check : UpdateCheckDecision.MissingToken;
    }
}

/// <summary>
/// Decides whether a release found on GitHub may replace the running version: strictly newer by SemVer 2,
/// and pre-releases only when the user opted in. Never a downgrade, never a reinstall of the same version.
/// </summary>
public static class UpdateVersionGate
{
    public static bool IsUpgrade(string current, string candidate, bool includePrerelease)
    {
        if (!SemVer.TryParse(current, out var have) || !SemVer.TryParse(candidate, out var want))
            return false;
        if (want.IsPrerelease && !includePrerelease)
            return false;
        return want.CompareTo(have) > 0;
    }

    private readonly record struct SemVer(int Major, int Minor, int Patch, string Prerelease) : IComparable<SemVer>
    {
        public bool IsPrerelease => Prerelease.Length > 0;

        public static bool TryParse(string? text, out SemVer version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            var s = text.Trim();
            if (s.StartsWith('v') || s.StartsWith('V'))
                s = s[1..];
            var plus = s.IndexOf('+', StringComparison.Ordinal);
            if (plus >= 0)
                s = s[..plus];
            var dash = s.IndexOf('-', StringComparison.Ordinal);
            var pre = dash >= 0 ? s[(dash + 1)..] : string.Empty;
            if (dash >= 0)
                s = s[..dash];
            if (dash >= 0 && pre.Length == 0)
                return false;
            var parts = s.Split('.');
            if (parts.Length != 3)
                return false;
            var numbers = new int[3];
            for (var i = 0; i < 3; i++)
            {
                if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit)
                    || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                    return false;
            }
            foreach (var id in pre.Split('.', StringSplitOptions.None))
            {
                if (pre.Length > 0 && (id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
                    return false;
            }
            version = new SemVer(numbers[0], numbers[1], numbers[2], pre);
            return true;
        }

        public int CompareTo(SemVer other)
        {
            var c = Major.CompareTo(other.Major);
            if (c != 0) return c;
            c = Minor.CompareTo(other.Minor);
            if (c != 0) return c;
            c = Patch.CompareTo(other.Patch);
            if (c != 0) return c;
            if (IsPrerelease != other.IsPrerelease)
                return IsPrerelease ? -1 : 1; // a release beats its own pre-release
            if (!IsPrerelease)
                return 0;
            var mine = Prerelease.Split('.');
            var theirs = other.Prerelease.Split('.');
            for (var i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
            {
                var mineNumeric = mine[i].All(char.IsAsciiDigit);
                var theirsNumeric = theirs[i].All(char.IsAsciiDigit);
                int r;
                if (mineNumeric && theirsNumeric)
                {
                    // Numeric identifiers compare by value; without leading zeros that is length, then digits.
                    var a = mine[i].TrimStart('0');
                    var b = theirs[i].TrimStart('0');
                    r = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
                }
                else if (mineNumeric != theirsNumeric)
                    r = mineNumeric ? -1 : 1;
                else
                    r = string.CompareOrdinal(mine[i], theirs[i]);
                if (r != 0) return r;
            }
            return mine.Length.CompareTo(theirs.Length);
        }
    }
}

/// <summary>
/// Shape checks for the GitHub token that reads releases of the private repository. The token is stored with
/// DPAPI like the provider key and is never logged or echoed; only <see cref="Mask"/> output may be shown.
/// </summary>
public static partial class GitHubAccessToken
{
    private static readonly string[] Prefixes = ["ghp_", "github_pat_", "gho_"];
    private const int MinimumLength = 20;

    public static string Normalize(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return token.Trim();
    }

    /// <summary>Null when the token looks like a GitHub token; otherwise a short reason suitable for the status line.</summary>
    public static string? RejectReason(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return "A GitHub token is required.";
        var t = token.Trim();
        if (t.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return "The token must not contain spaces or line breaks.";
        if (!Prefixes.Any(p => t.StartsWith(p, StringComparison.Ordinal)))
            return "That does not look like a GitHub token (expected ghp_, github_pat_ or gho_).";
        if (t.Length < MinimumLength)
            return "The token is too short.";
        return null;
    }

    /// <summary>
    /// Why a fine-grained token is the safe choice. Classic (<c>ghp_</c>) and OAuth (<c>gho_</c>) tokens cannot be
    /// limited to read-only access to one repository: a leaked <c>repo</c>-scoped token lets its holder publish a
    /// release that the updater would install (the build is not code-signed). Null for a fine-grained token.
    /// </summary>
    public static string? ScopeNote(string? token)
    {
        if (token is null || RejectReason(token) is not null)
            return null;
        var t = token.Trim();
        if (t.StartsWith("github_pat_", StringComparison.Ordinal))
            return null;
        return "This is a classic token. Prefer a fine-grained token limited to this repository with Contents: read only; a leaked classic token with repo scope could publish a release Boo would install.";
    }

    /// <summary>Prefix plus the last four characters, e.g. <c>ghp_…WXYZ</c>. Never more of the secret than that.</summary>
    public static string Mask(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var t = token.Trim();
        var prefix = Prefixes.FirstOrDefault(p => t.StartsWith(p, StringComparison.Ordinal)) ?? string.Empty;
        var body = t[prefix.Length..];
        var tail = body.Length > 4 ? body[^4..] : string.Empty;
        return prefix + "…" + tail;
    }

    [GeneratedRegex(@"(?:ghp_|github_pat_|gho_)[A-Za-z0-9_]+", RegexOptions.CultureInvariant)]
    internal static partial Regex TokenPattern();
}

public enum UpdateCheckOutcome
{
    NotChecked,
    Disabled,
    PortableBuild,
    MissingToken,
    Checking,
    UpToDate,
    UpdateAvailable,
    Downloading,
    ReadyToRestart,
    Failed
}

/// <summary>Text for the Settings &gt; Updates status line. Never contains a token.</summary>
public static class UpdateStatusText
{
    public static string Describe(UpdateCheckOutcome outcome, string? current = null, string? available = null, int percent = 0, string? error = null)
    {
        var have = string.IsNullOrWhiteSpace(current) ? "this version" : current.Trim();
        var next = string.IsNullOrWhiteSpace(available) ? "a new version" : available.Trim();
        var pct = Math.Clamp(percent, 0, 100).ToString(CultureInfo.InvariantCulture);
        return outcome switch
        {
            UpdateCheckOutcome.NotChecked => "Updates have not been checked yet.",
            UpdateCheckOutcome.Disabled => "Automatic update checks are off. Boo never contacts GitHub unless you turn them on.",
            UpdateCheckOutcome.PortableBuild => "This is a portable build. Updates apply to the installed version only (run Boo-win-Setup.exe).",
            UpdateCheckOutcome.MissingToken => "Add a GitHub token with read access to the Boo repository to check for updates.",
            UpdateCheckOutcome.Checking => "Checking GitHub Releases…",
            UpdateCheckOutcome.UpToDate => $"Boo {have} is up to date.",
            UpdateCheckOutcome.UpdateAvailable => $"Boo {next} is available (you have {have}). Press Install and restart.",
            UpdateCheckOutcome.Downloading => $"Downloading Boo {next}… {pct}%",
            UpdateCheckOutcome.ReadyToRestart => $"Boo {next} is downloaded. Press Install and restart to finish.",
            UpdateCheckOutcome.Failed => $"Update check failed: {SanitizeError(error)}",
            _ => "Updates have not been checked yet."
        };
    }

    /// <summary>Replaces anything shaped like a GitHub token with "[token]" and flattens line breaks.</summary>
    public static string SanitizeError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return "unknown error";
        var flat = GitHubAccessToken.TokenPattern().Replace(error, "[token]");
        flat = flat.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
        return string.Join(' ', flat.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
