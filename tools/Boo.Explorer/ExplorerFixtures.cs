using System.Diagnostics;
using System.IO;
using Boo.Core;
using Boo.Demo.Tests;
using Boo.LiveSmoke;
using Boo.Storage;

namespace Boo.Explorer;

// The four ways the explorer starts Boo. Every one reuses BooAppFixture (published Boo.exe, isolated BOO_DATA_DIR,
// Boo-window-only screenshots, Run-key restore) and seeds settings with speech OFF, so a typed question takes the text
// path and no voice (realtime) call is ever made.

internal static class Seeding
{
    public static void Settings(string dataDirectory) =>
        new SettingsStore(Path.Combine(dataDirectory, "settings.json"))
            .Save(new AppSettings(SpeechEnabled: false, Provider: new ProviderSettings(TextProvider: AiProvider.OpenAI)));

    /// <summary>
    /// A wrong OpenAI key that passes Boo's own shape check but is NOT key-shaped for the evidence key scan (no "sk-"
    /// prefix), so OpenAI answers 401 (free, not billed) and the evidence is never deleted as a leak.
    /// </summary>
    public static void WrongKey(string dataDirectory) =>
        new CredentialStore(Path.Combine(dataDirectory, AiProviders.CredentialFileName(AiProvider.OpenAI)))
            .Save("boo-explorer-wrong-key-" + Guid.NewGuid().ToString("N"));

    public static void CaptureBooWindowsOnly() =>
        Environment.SetEnvironmentVariable(LiveAppFixture.CaptureScopeVariable, "boo-windows");
}

/// <summary>No key at all: every AI action must fail politely, with zero requests.</summary>
public sealed class NoKeyFixture : BooAppFixture
{
    public NoKeyFixture() : base(dir => { Seeding.CaptureBooWindowsOnly(); Seeding.Settings(dir); }) { }
}

/// <summary>A wrong key: OpenAI answers 401 (not billed). The anti-mock guard runs first, so the 401 is OpenAI's own.</summary>
public sealed class WrongKeyFixture : BooAppFixture
{
    public WrongKeyFixture() : base(dir =>
    {
        Seeding.CaptureBooWindowsOnly();
        RealAiHostGuard.EnsureRealProviderHosts();
        Seeding.Settings(dir);
        Seeding.WrongKey(dir);
    }) { }
}

/// <summary>
/// No network for Boo only: a Windows Firewall rule blocks every outbound connection from the published Boo.exe
/// (needs admin; GitHub's runner is admin). The seeded key is the wrong one, so even if the block failed nothing is
/// billed, and a 401 instead of a network message would show the block did not hold. The rule is removed on dispose.
/// </summary>
public sealed class NoNetworkFixture : BooAppFixture, IDisposable
{
    public const string RuleName = "Boo explorer: no network for Boo.exe";
    public static string BlockResult { get; private set; } = "<not run>";

    public NoNetworkFixture() : base(dir =>
    {
        Seeding.CaptureBooWindowsOnly();
        BlockResult = Netsh($"advfirewall firewall add rule name=\"{RuleName}\" dir=out action=block program=\"{ResolveExe()}\" enable=yes");
        Seeding.Settings(dir);
        Seeding.WrongKey(dir);
    }) { }

    // Interface re-implementation: BooAppFixture.Dispose is not virtual, and xunit disposes through IDisposable.
    void IDisposable.Dispose()
    {
        try { Dispose(); }
        finally { Netsh($"advfirewall firewall delete rule name=\"{RuleName}\""); }
    }

    private static string ResolveExe() => Path.GetFullPath(Environment.GetEnvironmentVariable("BOO_UI_EXE")
        ?? throw new InvalidOperationException("BOO_UI_EXE is not set; the no-network launch needs the published Boo.exe path."));

    internal static string Netsh(string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo("netsh", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return $"exit={p.ExitCode} {output.Trim()}";
    }
}

/// <summary>
/// A second launch on a COPY of an earlier launch's data folder (settings, saved key, focus files), used after the
/// explorer kills Boo mid-reply. It proves Boo comes back on the same data the way a user's restart does.
/// </summary>
public sealed class RelaunchFixture : BooAppFixture
{
    public RelaunchFixture(string copyFrom) : base(dir => { Seeding.CaptureBooWindowsOnly(); CopyTree(copyFrom, dir); }) { }

    private static void CopyTree(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
