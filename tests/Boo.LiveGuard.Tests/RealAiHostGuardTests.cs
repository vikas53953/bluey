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
    [InlineData("fec0::", "site-local")]
    [InlineData("fec0::1", "site-local")]
    [InlineData("feff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", "site-local")]
    [InlineData("198.18.0.0", "198.18.0.0/15")]
    [InlineData("198.18.0.21", "198.18.0.0/15")]
    [InlineData("198.19.255.255", "198.18.0.0/15")]
    [InlineData("64:ff9b::a00:5", "NAT64")]                 // 10.0.0.5 behind NAT64
    [InlineData("64:ff9b::7f00:1", "NAT64")]                // 127.0.0.1 behind NAT64
    [InlineData("64:ff9b:1::1", "local NAT64")]
    [InlineData("2002:c0a8:101::1", "6to4")]                // 192.168.1.1 inside 6to4
    [InlineData("2002:ffff:ffff:ffff:ffff:ffff:ffff:ffff", "6to4")]
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
    [InlineData("198.17.255.255")]           // just below 198.18/15
    [InlineData("198.20.0.0")]               // just above 198.18/15
    [InlineData("2001:ffff:ffff:ffff:ffff:ffff:ffff:ffff")] // just below 2002::/16
    [InlineData("2003::1")]                  // just above 2002::/16
    [InlineData("64:ff9a:ffff:ffff:ffff:ffff:ffff:ffff")]   // just below 64:ff9b::/96
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
    public void Provider_hosts_come_from_Boos_own_provider_list()
    {
        var fromBoo = Enum.GetValues<Boo.Core.AiProvider>().Select(p => Boo.Core.AiProviders.Origin(p).Host);

        Assert.Equal(fromBoo, RealAiHostGuard.ProviderHosts);
    }

    [Fact]
    public void Guard_fails_closed_naming_the_host_and_range_when_a_provider_host_is_redirected_to_the_LAN()
    {
        var error = Assert.Throws<InvalidOperationException>(() => RealAiHostGuard.EnsureRealProviderHosts(
            host => host == "api.openai.com" ? new[] { IPAddress.Parse("192.168.1.20") } : new[] { IPAddress.Parse("8.8.8.8") },
            environment: _ => null,
            useSystemProxy: false));

        Assert.StartsWith("LIVE SMOKE FAILED CLOSED", error.Message);
        Assert.Contains("api.openai.com resolves to 192.168.1.20 is private LAN 192.168.0.0/16", error.Message);
    }

    [Fact]
    public void Guard_passes_and_reports_what_it_accepted_when_every_provider_host_is_public_and_no_proxy_is_set()
    {
        var accepted = RealAiHostGuard.EnsureRealProviderHosts(
            _ => [IPAddress.Parse("162.159.140.245"), IPAddress.Parse("2606:4700:7::f3")],
            environment: _ => null,
            useSystemProxy: false);

        Assert.Equal(3, accepted.Count);
        Assert.All(accepted, line => Assert.EndsWith("162.159.140.245, 2606:4700:7::f3 (all public)", line));
    }

    // ---- a proxy between Boo and the provider is a mock in disguise ----

    [Theory]
    [InlineData("HTTPS_PROXY")]
    [InlineData("HTTP_PROXY")]
    [InlineData("ALL_PROXY")]
    public void Proxy_environment_variable_is_rejected(string variable)
    {
        var problems = RealAiHostGuard.ProxyProblems(
            ["api.openai.com"], name => name == variable ? "http://127.0.0.1:8080" : null, defaultProxy: null);

        Assert.Equal($"proxy variable {variable} is set", Assert.Single(problems));
    }

    [Fact]
    public void Default_HTTP_proxy_that_would_carry_provider_requests_is_rejected()
    {
        var problems = RealAiHostGuard.ProxyProblems(
            ["api.openai.com"], _ => null, new WebProxy("http://192.168.1.5:3128"));

        Assert.Equal("requests to api.openai.com would go through proxy 192.168.1.5:3128", Assert.Single(problems));
    }

    [Fact]
    public void Proxy_that_bypasses_the_provider_hosts_is_not_a_problem()
    {
        var proxy = new WebProxy("http://192.168.1.5:3128", BypassOnLocal: false, BypassList: [@"api\.openai\.com"]);

        Assert.Empty(RealAiHostGuard.ProxyProblems(["api.openai.com"], _ => null, proxy));
    }

    [Fact]
    public void Guard_fails_closed_when_a_proxy_variable_is_set_even_if_DNS_is_public()
    {
        var error = Assert.Throws<InvalidOperationException>(() => RealAiHostGuard.EnsureRealProviderHosts(
            _ => [IPAddress.Parse("162.159.140.245")],
            environment: name => name == "HTTPS_PROXY" ? "http://10.0.0.2:8080" : null,
            useSystemProxy: false));

        Assert.Contains("proxy variable HTTPS_PROXY is set", error.Message);
    }

    // ---- the real thing: real public API hosts, real DNS, this machine's real proxy settings ----

    [Fact]
    public void Real_public_API_host_resolved_through_real_DNS_passes()
    {
        var addresses = Dns.GetHostAddresses("api.openai.com");
        Assert.NotEmpty(addresses);

        var problems = RealAiHostGuard.Problems(["api.openai.com"], Dns.GetHostAddresses);

        Assert.Empty(problems);
    }

    [Fact]
    public void Guard_with_its_real_defaults_passes_on_a_clean_machine()
    {
        // No fakes: Dns.GetHostAddresses, the process environment and HttpClient.DefaultProxy, exactly as the live run.
        var accepted = RealAiHostGuard.EnsureRealProviderHosts();

        Assert.Equal(RealAiHostGuard.ProviderHosts.Count, accepted.Count);
        Assert.Contains(accepted, line => line.StartsWith("api.openai.com -> ", StringComparison.Ordinal));
    }
}
