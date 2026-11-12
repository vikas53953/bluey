using System.Net;
using System.Net.Sockets;
using Boo.LiveSmoke;

namespace Boo.LiveGuard.Tests;

/// <summary>
/// The live-AI smoke must refuse to run when any AI provider host points anywhere a mock could live: loopback, every
/// private/LAN range, link-local, unique-local IPv6, carrier-grade NAT, and the same addresses wrapped in IPv6.
/// Each range is checked at its first address, a middle address and its last address; the addresses just outside
/// each edge must still pass, so a range that is too wide fails here too.
/// </summary>
public sealed class RealAiHostGuardTests
{
    // ---- every blocked range: first, middle, last ----

    [Theory]
    [InlineData("127.0.0.1", "loopback")]
    [InlineData("127.255.255.255", "loopback")]
    [InlineData("::1", "loopback")]
    [InlineData("::", "unspecified")]
    [InlineData("0.0.0.0", "'this network'")]
    [InlineData("10.0.0.0", "10.0.0.0/8")]
    [InlineData("10.20.30.40", "10.0.0.0/8")]
    [InlineData("10.255.255.255", "10.0.0.0/8")]
    [InlineData("172.16.0.0", "172.16.0.0/12")]
    [InlineData("172.20.10.5", "172.16.0.0/12")]
    [InlineData("172.31.255.255", "172.16.0.0/12")]
    [InlineData("192.168.0.0", "192.168.0.0/16")]
    [InlineData("192.168.1.20", "192.168.0.0/16")]
    [InlineData("192.168.255.255", "192.168.0.0/16")]
    [InlineData("169.254.0.0", "169.254.0.0/16")]
    [InlineData("169.254.169.254", "169.254.0.0/16")]
    [InlineData("169.254.255.255", "169.254.0.0/16")]
    [InlineData("100.64.0.0", "100.64.0.0/10")]
    [InlineData("100.100.100.100", "100.64.0.0/10")]
    [InlineData("100.127.255.255", "100.64.0.0/10")]
    [InlineData("fc00::", "fc00::/7")]
    [InlineData("fd12:3456:789a::1", "fc00::/7")]
    [InlineData("fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", "fc00::/7")]
    [InlineData("fe80::", "fe80::/10")]
    [InlineData("fe80::1", "fe80::/10")]
    [InlineData("fe80::1%12", "fe80::/10")]
    [InlineData("febf:ffff:ffff:ffff:ffff:ffff:ffff:ffff", "fe80::/10")]
    [InlineData("224.0.0.1", "multicast")]
    [InlineData("255.255.255.255", "broadcast")]
    [InlineData("ff02::1", "multicast")]
    public void Address_in_a_local_private_or_LAN_range_is_rejected(string address, string expectedRangeName)
    {
        var reason = RealAiHostGuard.RejectReason(IPAddress.Parse(address));

        Assert.NotNull(reason);
        Assert.Contains(expectedRangeName, reason);
    }

    // ---- the same private addresses wrapped in IPv6 must not slip through ----

    [Theory]
    [InlineData("::ffff:127.0.0.1", "loopback")]
    [InlineData("::ffff:10.0.0.5", "10.0.0.0/8")]
    [InlineData("::ffff:172.16.0.9", "172.16.0.0/12")]
    [InlineData("::ffff:192.168.1.20", "192.168.0.0/16")]
    [InlineData("::ffff:169.254.10.10", "169.254.0.0/16")]
    [InlineData("::10.0.0.5", "IPv4-compatible")]
    public void Private_address_wrapped_in_IPv6_is_rejected(string address, string expectedRangeName)
    {
        var reason = RealAiHostGuard.RejectReason(IPAddress.Parse(address));

        Assert.NotNull(reason);
        Assert.Contains(expectedRangeName, reason);
    }

    // ---- public addresses, including the ones right next to each blocked edge, pass ----

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("162.159.140.245")]          // a real api.openai.com answer seen on 12 Nov 2026
    [InlineData("9.255.255.255")]            // just below 10/8
    [InlineData("11.0.0.0")]                 // just above 10/8
    [InlineData("172.15.255.255")]           // just below 172.16/12
    [InlineData("172.32.0.0")]               // just above 172.16/12
    [InlineData("192.167.255.255")]          // just below 192.168/16
    [InlineData("192.169.0.0")]              // just above 192.168/16
    [InlineData("169.253.255.255")]          // just below 169.254/16
    [InlineData("169.255.0.0")]              // just above 169.254/16
    [InlineData("100.63.255.255")]           // just below 100.64/10
    [InlineData("100.128.0.0")]              // just above 100.64/10
    [InlineData("126.255.255.255")]          // just below 127/8
    [InlineData("128.0.0.0")]                // just above 127/8
    [InlineData("2606:4700:7::f3")]          // a real api.openai.com IPv6 answer seen on 12 Nov 2026
    [InlineData("2001:4860:4860::8888")]
    [InlineData("fbff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")] // just below fc00::/7
    [InlineData("fe00::")]                   // just above fc00::/7
    [InlineData("fe7f:ffff:ffff:ffff:ffff:ffff:ffff:ffff")] // just below fe80::/10
    [InlineData("fec0::")]                   // just above fe80::/10
    [InlineData("::ffff:8.8.8.8")]           // a public address wrapped in IPv6 is still public
    public void Public_internet_address_passes(string address)
    {
        Assert.Null(RealAiHostGuard.RejectReason(IPAddress.Parse(address)));
    }

    // ---- hostnames that resolve to a blocked range are rejected ----

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.5.4")]
    [InlineData("192.168.50.10")]
    [InlineData("169.254.1.1")]
    [InlineData("100.101.102.103")]
    [InlineData("fd00::1")]
    [InlineData("fe80::abcd")]
    [InlineData("::ffff:192.168.0.2")]
    public void Provider_hostname_that_resolves_to_a_blocked_range_is_rejected(string resolvedTo)
    {
        var problems = RealAiHostGuard.Problems(["api.openai.com"], _ => [IPAddress.Parse(resolvedTo)]);

        var problem = Assert.Single(problems);
        Assert.StartsWith("api.openai.com resolves to ", problem);
    }

    [Fact]
    public void One_private_answer_among_public_answers_is_enough_to_reject()
    {
        var problems = RealAiHostGuard.Problems(
            ["api.openai.com"],
            _ => [IPAddress.Parse("162.159.140.245"), IPAddress.Parse("192.168.1.50"), IPAddress.Parse("2606:4700:7::f3")]);

        var problem = Assert.Single(problems);
        Assert.Contains("192.168.1.50", problem);
    }

    [Fact]
    public void Host_that_does_not_resolve_is_a_problem_not_a_pass()
    {
        var problems = RealAiHostGuard.Problems(
            ["api.openai.com"], _ => throw new SocketException((int)SocketError.HostNotFound));

        Assert.Contains("does not resolve", Assert.Single(problems));
    }

    [Fact]
    public void Host_that_resolves_to_nothing_is_a_problem_not_a_pass()
    {
        var problems = RealAiHostGuard.Problems(["api.openai.com"], _ => []);

        Assert.Contains("resolves to no address", Assert.Single(problems));
    }

    [Fact]
    public void An_IP_literal_host_is_checked_as_itself_without_a_lookup()
    {
        var problems = RealAiHostGuard.Problems(["10.0.0.7"], _ => throw new InvalidOperationException("no lookup expected"));

        Assert.Contains("10.0.0.0/8", Assert.Single(problems));
    }

    [Fact]
    public void Guard_checks_every_AI_provider_host()
    {
        Assert.Equal(
            new[] { "api.openai.com", "api.anthropic.com", "generativelanguage.googleapis.com" },
            RealAiHostGuard.ProviderHosts);
    }

    [Fact]
    public void Guard_fails_closed_naming_the_host_and_range_when_a_provider_host_is_redirected_to_the_LAN()
    {
        var error = Assert.Throws<InvalidOperationException>(() => RealAiHostGuard.EnsureRealProviderHosts(
            host => host == "api.openai.com" ? new[] { IPAddress.Parse("192.168.1.20") } : new[] { IPAddress.Parse("8.8.8.8") }));

        Assert.StartsWith("LIVE SMOKE FAILED CLOSED", error.Message);
        Assert.Contains("api.openai.com resolves to 192.168.1.20 is private LAN 192.168.0.0/16", error.Message);
    }

    [Fact]
    public void Guard_passes_when_every_provider_host_resolves_to_public_addresses()
    {
        RealAiHostGuard.EnsureRealProviderHosts(_ => [IPAddress.Parse("162.159.140.245"), IPAddress.Parse("2606:4700:7::f3")]);
    }

    // ---- the real thing: a real public API host, resolved through real DNS, passes ----

    [Fact]
    public void Real_public_API_host_resolved_through_real_DNS_passes()
    {
        var addresses = Dns.GetHostAddresses("api.openai.com");
        Assert.NotEmpty(addresses);

        var problems = RealAiHostGuard.Problems(["api.openai.com"], Dns.GetHostAddresses);

        Assert.Empty(problems);
    }
}
