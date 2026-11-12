using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Boo.Core;

namespace Boo.LiveSmoke;

/// <summary>
/// Anti-mock guard for the live AI smoke. Before Boo starts, Boo's traffic to every AI provider must go straight to a
/// public internet address: every provider host must resolve ONLY to public addresses, and no proxy may sit in
/// between. A mock answering for api.openai.com has to live somewhere the runner reaches without the real internet:
/// this machine (loopback), the LAN (private ranges), a link-local, site-local or unique-local IPv6 address, a
/// carrier-grade-NAT overlay (Tailscale and similar), a fake-IP proxy range, an IPv6 translation of any of those, or a
/// proxy that intercepts the request. Any of those fails the run closed. Hosts-file pins are caught too, because the
/// system resolver reads the hosts file. tests/Boo.LiveGuard.Tests exercises every range on every windows-ci run.
/// Not covered: a trusted root certificate planted on the machine (the guard checks where traffic goes, not who signs).
/// </summary>
public static class RealAiHostGuard
{
    /// <summary>Every AI provider host Boo talks to, taken from Boo's own provider list so the two cannot drift.</summary>
    public static IReadOnlyList<string> ProviderHosts { get; } =
        Enum.GetValues<AiProvider>().Select(p => AiProviders.Origin(p).Host).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Address ranges that can never be a real provider endpoint, with a plain-words name for each.</summary>
    public static readonly IReadOnlyList<(IPNetwork Range, string Name)> BlockedRanges =
    [
        (IPNetwork.Parse("0.0.0.0/8"), "'this network' 0.0.0.0/8"),
        (IPNetwork.Parse("10.0.0.0/8"), "private LAN 10.0.0.0/8"),
        (IPNetwork.Parse("100.64.0.0/10"), "carrier-grade NAT / overlay 100.64.0.0/10"),
        (IPNetwork.Parse("127.0.0.0/8"), "loopback 127.0.0.0/8"),
        (IPNetwork.Parse("169.254.0.0/16"), "link-local 169.254.0.0/16"),
        (IPNetwork.Parse("172.16.0.0/12"), "private LAN 172.16.0.0/12"),
        (IPNetwork.Parse("192.168.0.0/16"), "private LAN 192.168.0.0/16"),
        (IPNetwork.Parse("198.18.0.0/15"), "benchmark / fake-IP proxy 198.18.0.0/15"),
        (IPNetwork.Parse("224.0.0.0/4"), "multicast 224.0.0.0/4"),
        (IPNetwork.Parse("240.0.0.0/4"), "reserved/broadcast 240.0.0.0/4"),
        (IPNetwork.Parse("::/96"), "unspecified, loopback or IPv4-compatible ::/96"),
        (IPNetwork.Parse("64:ff9b::/96"), "NAT64 translation 64:ff9b::/96"),
        (IPNetwork.Parse("64:ff9b:1::/48"), "local NAT64 translation 64:ff9b:1::/48"),
        (IPNetwork.Parse("2002::/16"), "6to4 tunnel 2002::/16"),
        (IPNetwork.Parse("fc00::/7"), "unique-local IPv6 fc00::/7"),
        (IPNetwork.Parse("fe80::/10"), "link-local IPv6 fe80::/10"),
        (IPNetwork.Parse("fec0::/10"), "site-local IPv6 fec0::/10"),
        (IPNetwork.Parse("ff00::/8"), "multicast IPv6 ff00::/8"),
    ];

    /// <summary>Environment variables that route HTTP(S) through a proxy for .NET and most tools.</summary>
    public static readonly IReadOnlyList<string> ProxyVariables = ["HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY"];

    /// <summary>Why this address cannot be a real provider, or null when it is a public internet address.</summary>
    public static string? RejectReason(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var plain = address;
        if (plain.AddressFamily == AddressFamily.InterNetworkV6)
        {
            plain = new IPAddress(plain.GetAddressBytes()); // drop any %scope id
            if (plain.IsIPv4MappedToIPv6) plain = plain.MapToIPv4();
        }
        foreach (var (range, name) in BlockedRanges)
        {
            if (range.Contains(plain)) return $"{address} is {name}";
        }
        return null;
    }

    /// <summary>
    /// Every problem with these hosts: a host that does not resolve, resolves to nothing, or resolves to ANY blocked
    /// address (one private answer among public ones is enough to fail). An IP literal is checked as itself.
    /// </summary>
    public static IReadOnlyList<string> Problems(IEnumerable<string> hosts, Func<string, IPAddress[]> resolve) =>
        Check(hosts, resolve).Problems;

    /// <summary>
    /// Every way a proxy could sit between Boo and the provider: a proxy environment variable, or a proxy that .NET's
    /// default HTTP stack (the one Boo's HttpClient uses) would send a provider request to.
    /// </summary>
    public static IReadOnlyList<string> ProxyProblems(IEnumerable<string> hosts, Func<string, string?> environment, IWebProxy? defaultProxy)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(environment);
        var problems = new List<string>();
        foreach (var name in ProxyVariables)
        {
            if (!string.IsNullOrWhiteSpace(environment(name))) problems.Add($"proxy variable {name} is set");
        }
        if (defaultProxy is not null)
        {
            foreach (var host in hosts)
            {
                var target = new Uri($"https://{host}/");
                if (defaultProxy.IsBypassed(target)) continue;
                var via = defaultProxy.GetProxy(target);
                if (via is not null && via != target) problems.Add($"requests to {host} would go through proxy {via.Host}:{via.Port}");
            }
        }
        return problems;
    }

    /// <summary>
    /// Throws (fails the run closed) unless every provider host resolves only to public addresses and no proxy is in
    /// the way. Returns one line per host with the addresses it accepted, for the run's evidence.
    /// </summary>
    public static IReadOnlyList<string> EnsureRealProviderHosts(
        Func<string, IPAddress[]>? resolve = null,
        Func<string, string?>? environment = null,
        IWebProxy? defaultProxy = null,
        bool useSystemProxy = true)
    {
        var (problems, accepted) = Check(ProviderHosts, resolve ?? Dns.GetHostAddresses);
        var proxy = defaultProxy ?? (useSystemProxy ? HttpClient.DefaultProxy : null);
        var all = problems.Concat(ProxyProblems(ProviderHosts, environment ?? Environment.GetEnvironmentVariable, proxy)).ToList();
        if (all.Count > 0)
        {
            throw new InvalidOperationException(
                "LIVE SMOKE FAILED CLOSED: an AI host points at a local, private or LAN address, or a proxy is in the " +
                "way, so a mock could answer instead of the real provider: " + string.Join("; ", all));
        }
        return accepted;
    }

    private static (IReadOnlyList<string> Problems, IReadOnlyList<string> Accepted) Check(
        IEnumerable<string> hosts, Func<string, IPAddress[]> resolve)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(resolve);
        var problems = new List<string>();
        var accepted = new List<string>();
        foreach (var host in hosts)
        {
            IPAddress[] addresses;
            if (IPAddress.TryParse(host, out var literal))
            {
                addresses = [literal];
            }
            else
            {
                try
                {
                    addresses = resolve(host) ?? [];
                }
                catch (Exception e) when (e is SocketException or ArgumentException)
                {
                    problems.Add($"{host} does not resolve ({e.Message})");
                    continue;
                }
            }

            if (addresses.Length == 0)
            {
                problems.Add($"{host} resolves to no address");
                continue;
            }
            var before = problems.Count;
            foreach (var address in addresses)
            {
                if (RejectReason(address) is { } reason) problems.Add($"{host} resolves to {reason}");
            }
            if (problems.Count == before) accepted.Add($"{host} -> {string.Join(", ", addresses.Select(a => a.ToString()))} (all public)");
        }
        return (problems, accepted);
    }
}
