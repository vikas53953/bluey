using System.Net;
using System.Net.Sockets;

namespace Boo.LiveSmoke;

/// <summary>
/// Anti-mock guard for the live AI smoke: before Boo starts, every AI provider host must resolve ONLY to public
/// internet addresses. A mock answering for api.openai.com has to live somewhere the runner can reach without the real
/// internet: this machine (loopback), the LAN (private ranges), a link-local or unique-local IPv6 address, or a
/// carrier-grade-NAT overlay (Tailscale and similar). Any of those, written directly or wrapped in IPv6
/// (::ffff:10.0.0.5), fails the run closed. Hosts-file pins are caught too, because the system resolver reads the hosts
/// file. The test file tests/Boo.LiveGuard.Tests exercises every range below on every windows-ci run.
/// </summary>
public static class RealAiHostGuard
{
    /// <summary>Every AI provider host Boo talks to (OpenAI text and voice, Anthropic, Gemini).</summary>
    public static readonly IReadOnlyList<string> ProviderHosts =
        ["api.openai.com", "api.anthropic.com", "generativelanguage.googleapis.com"];

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
        (IPNetwork.Parse("224.0.0.0/4"), "multicast 224.0.0.0/4"),
        (IPNetwork.Parse("240.0.0.0/4"), "reserved/broadcast 240.0.0.0/4"),
        (IPNetwork.Parse("::/96"), "unspecified, loopback or IPv4-compatible ::/96"),
        (IPNetwork.Parse("fc00::/7"), "unique-local IPv6 fc00::/7"),
        (IPNetwork.Parse("fe80::/10"), "link-local IPv6 fe80::/10"),
        (IPNetwork.Parse("ff00::/8"), "multicast IPv6 ff00::/8"),
    ];

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
    public static IReadOnlyList<string> Problems(IEnumerable<string> hosts, Func<string, IPAddress[]> resolve)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(resolve);
        var problems = new List<string>();
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
            foreach (var address in addresses)
            {
                if (RejectReason(address) is { } reason) problems.Add($"{host} resolves to {reason}");
            }
        }
        return problems;
    }

    /// <summary>Throws (fails the run closed) unless every provider host resolves only to public addresses.</summary>
    public static void EnsureRealProviderHosts(Func<string, IPAddress[]>? resolve = null)
    {
        var problems = Problems(ProviderHosts, resolve ?? Dns.GetHostAddresses);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "LIVE SMOKE FAILED CLOSED: an AI host points at a local, private or LAN address, so a mock could answer " +
                "instead of the real provider: " + string.Join("; ", problems));
        }
    }
}
