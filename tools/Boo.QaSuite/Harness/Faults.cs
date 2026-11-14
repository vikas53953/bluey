using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Boo.Demo.Tests;

namespace Boo.QaSuite;

/// <summary>
/// Planted faults for the error-path checks: network down (a Windows Firewall rule that blocks Boo.exe only) and a
/// provider that never answers (api.openai.com pointed at a local server that accepts the connection and then stays
/// silent). They change machine settings, so they run ONLY on GitHub's own throwaway VM and refuse anywhere else.
/// Every fault is undone when disposed; the workflow also runs an always-on clean-up step afterwards.
/// </summary>
public static class Faults
{
    public const string FirewallRuleName = "boo-qa-network-down";
    public const string HostsMarker = "# boo-qa-timeout";
    private static readonly string HostsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    public static void RequireThrowawayVm()
    {
        if (!BooAppFixture.OnGitHubHostedRunner())
        {
            throw new InvalidOperationException(
                "REFUSED: the error-path checks change the firewall and the hosts file, so they run only on GitHub's own " +
                "VM (RUNNER_ENVIRONMENT=github-hosted), never on a personal PC. Nothing was changed.");
        }
    }

    /// <summary>Blocks every outbound connection from Boo.exe (and only Boo.exe). Firewall profiles are switched on for
    /// the duration if the VM has them off, then put back exactly as they were.</summary>
    public static IDisposable BlockOutbound(string exePath, Action<string> log)
    {
        RequireThrowawayVm();
        var before = PowerShell("(Get-NetFirewallProfile | ForEach-Object { \"$($_.Name)=$($_.Enabled)\" }) -join ','");
        log($"firewall profiles before: {before}");
        PowerShell("Set-NetFirewallProfile -All -Enabled True");
        PowerShell($"New-NetFirewallRule -DisplayName '{FirewallRuleName}' -Direction Outbound -Program '{exePath.Replace("'", "''")}' -Action Block | Out-Null");
        log($"firewall rule '{FirewallRuleName}' blocks outbound for {exePath}");
        return new Undo(() =>
        {
            PowerShell($"Remove-NetFirewallRule -DisplayName '{FirewallRuleName}' -ErrorAction SilentlyContinue");
            foreach (var pair in before.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = pair.Split('=');
                if (parts.Length == 2 && parts[0].All(char.IsLetter) && (parts[1] == "True" || parts[1] == "False"))
                    PowerShell($"Set-NetFirewallProfile -Name {parts[0]} -Enabled {parts[1]}");
            }
            log("firewall rule removed, profiles restored");
        });
    }

    /// <summary>
    /// Points api.openai.com at 127.0.0.1:443, where a listener accepts every connection and never sends a byte, so
    /// Boo's request can only end by its own timeout. No answer is ever faked: nothing is sent back at all.
    /// </summary>
    public static IDisposable SilentProvider(Action<string> log)
    {
        RequireThrowawayVm();
        var listener = new TcpListener(IPAddress.Loopback, 443);
        listener.Start();
        var held = new List<TcpClient>();
        var stop = new CancellationTokenSource();
        var accepted = 0;
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(stop.Token);
                    lock (held) held.Add(client);
                    Interlocked.Increment(ref accepted);
                }
                catch (Exception)
                {
                    return;
                }
            }
        });
        File.AppendAllText(HostsFile, $"{Environment.NewLine}127.0.0.1 api.openai.com {HostsMarker}{Environment.NewLine}");
        Run("ipconfig", "/flushdns");
        log("hosts: api.openai.com -> 127.0.0.1 (silent listener on 443)");
        return new Undo(() =>
        {
            RemoveHostsPins();
            stop.Cancel();
            listener.Stop();
            lock (held) foreach (var c in held) c.Dispose();
            log($"hosts restored, silent listener stopped (it accepted {accepted} connection(s))");
        });
    }

    public static void RemoveHostsPins()
    {
        var lines = File.ReadAllLines(HostsFile).Where(l => !l.Contains(HostsMarker, StringComparison.Ordinal)).ToArray();
        File.WriteAllLines(HostsFile, lines);
        Run("ipconfig", "/flushdns");
    }

    // -EncodedCommand (UTF-16LE, base64): no quoting rules to get wrong.
    private static string PowerShell(string command) =>
        Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
            Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("$ErrorActionPreference = 'Stop'; " + command)));

    private static string Run(string file, string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"TEST SETUP: '{file}' failed (exit {p.ExitCode}): {stderr.Trim()}");
        return stdout.Trim();
    }

    private sealed class Undo(Action undo) : IDisposable
    {
        private Action? _undo = undo;

        public void Dispose()
        {
            Interlocked.Exchange(ref _undo, null)?.Invoke();
        }
    }
}
