using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Boo.LiveSmoke;
using Xunit.Abstractions;

namespace Boo.QaSuite.AntiMock;

/// <summary>
/// Proof that a green run used the real app and the real provider. Runs BEFORE any live step and before any planted
/// fault. No Boo is launched and no AI request is made.
/// AM0 the published app folder holds no mock or test code (no QA mock server, no test framework, no test project).
/// AM1 no AI host is redirected: every one resolves only to public internet addresses, no proxy, no hosts-file pin.
/// AM2 no planted root certificate for an AI host, and a real TLS handshake with api.openai.com validates against
///     Windows' trusted roots with a certificate that is not self-signed (a mock needs one of those to impersonate it).
/// </summary>
public sealed class AntiMockTests(ITestOutputHelper output)
{
    private static readonly string[] ForbiddenInShippedBuild =
    [
        "Boo.QaMockAi", "Boo.LiveSmoke", "Boo.VerifyLive", "Boo.QaSuite", "Boo.LiveProbe", "Boo.CaptureProof",
        "Boo.QaWorkspace", ".Tests.", "xunit", "FlaUI", "Moq", "NSubstitute", "FakeItEasy", "testhost",
        "Microsoft.TestPlatform", "Microsoft.VisualStudio.TestPlatform", "Microsoft.NET.Test.Sdk",
    ];

    private static readonly HashSet<string> BooAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Boo.dll", "Boo.exe", "Boo.Core.dll", "Boo.Windows.dll", "Boo.Providers.dll", "Boo.Storage.dll",
    };

    private static string Evidence => Environment.GetEnvironmentVariable("BOO_UI_ARTIFACTS") is { Length: > 0 } e ? e : Path.GetTempPath();

    private static void Write(string name, IEnumerable<string> lines)
    {
        Directory.CreateDirectory(Evidence);
        File.WriteAllLines(Path.Combine(Evidence, name + ".txt"), lines);
    }

    [Fact]
    public void AM0_ShippedBuild_HoldsNoMockOrTestCode()
    {
        var exe = Environment.GetEnvironmentVariable("BOO_UI_EXE");
        Assert.False(string.IsNullOrWhiteSpace(exe), "TEST SETUP: BOO_UI_EXE is not set, so the shipped build could not be checked.");
        Assert.True(File.Exists(exe), $"TEST SETUP: {exe} does not exist.");
        var folder = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var forbidden = files.Where(f => ForbiddenInShippedBuild.Any(p => Path.GetFileName(f).Contains(p, StringComparison.OrdinalIgnoreCase))).ToList();
        var unknownBoo = files.Where(f => Path.GetFileName(f).StartsWith("Boo", StringComparison.OrdinalIgnoreCase)
                                          && (f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                          && !BooAssemblies.Contains(Path.GetFileName(f))).ToList();
        string sha;
        using (var s = File.OpenRead(exe!)) sha = Convert.ToHexString(SHA256.HashData(s));
        Write("qa-am0-shipped-build", new[]
        {
            $"folder={folder}", $"files={files.Count}", $"Boo.exe sha256={sha}",
            $"forbidden={(forbidden.Count == 0 ? "none" : string.Join(", ", forbidden))}",
            $"unknownBooAssemblies={(unknownBoo.Count == 0 ? "none" : string.Join(", ", unknownBoo))}",
            "--- Boo assemblies ---",
        }.Concat(files.Where(f => Path.GetFileName(f).StartsWith("Boo", StringComparison.OrdinalIgnoreCase))));
        Assert.True(files.Count > 10, $"The published folder looks empty ({files.Count} files).");
        Assert.Empty(forbidden);
        Assert.Empty(unknownBoo);
    }

    [Fact]
    public void AM1_NoAiHostRedirected_NoProxy_NoHostsPin()
    {
        var hostsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
        var pins = File.ReadAllLines(hostsFile)
            .Select(l => l.Split('#')[0].Trim())
            .Where(l => l.Length > 0 && RealAiHostGuard.ProviderHosts.Any(h => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Skip(1)
                .Contains(h, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        IReadOnlyList<string> accepted = [];
        Exception? guard = null;
        try
        {
            accepted = RealAiHostGuard.EnsureRealProviderHosts();
        }
        catch (InvalidOperationException ex)
        {
            guard = ex;
        }
        Write("qa-am1-hosts", new[] { $"hostsPins={(pins.Count == 0 ? "none" : string.Join(" | ", pins))}", $"guard={(guard is null ? "passed" : guard.Message)}" }.Concat(accepted));
        Assert.Empty(pins);
        Assert.Null(guard);
    }

    [Fact]
    public void AM2_NoPlantedRootCertificate_AndOpenAIsRealCertificateValidates()
    {
        var planted = new List<string>();
        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            foreach (var name in new[] { StoreName.Root, StoreName.AuthRoot, StoreName.CertificateAuthority })
            {
                using var store = new X509Store(name, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                foreach (var cert in store.Certificates)
                {
                    if (RealAiHostGuard.ProviderHosts.Any(h => cert.Subject.Contains(h, StringComparison.OrdinalIgnoreCase)))
                        planted.Add($"{location}/{name}: {cert.Subject} (thumbprint {cert.Thumbprint})");
                }
            }
        }

        SslPolicyErrors errors = SslPolicyErrors.None;
        string leafSubject = "", leafIssuer = "", root = "";
        var chainLines = new List<string>();
        using (var tcp = new TcpClient())
        {
            tcp.Connect("api.openai.com", 443);
            using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, chain, e) =>
            {
                errors = e;
                if (cert is not null)
                {
                    leafSubject = cert.Subject;
                    leafIssuer = cert.Issuer;
                }
                if (chain is not null)
                {
                    foreach (var element in chain.ChainElements) chainLines.Add($"  {element.Certificate.Subject}");
                    root = chain.ChainElements.Count > 0 ? chain.ChainElements[^1].Certificate.Subject : "";
                }
                return true; // decided below, so the failure message can name the problem
            });
            ssl.AuthenticateAsClient("api.openai.com");
        }
        Write("qa-am2-certificates", new[]
        {
            $"plantedRootsForAiHosts={(planted.Count == 0 ? "none" : string.Join(" | ", planted))}",
            $"tlsPolicyErrors={errors}", $"leaf={leafSubject}", $"issuer={leafIssuer}", $"root={root}", "chain:",
        }.Concat(chainLines));
        output.WriteLine($"api.openai.com: leaf '{leafSubject}', root '{root}', errors {errors}");
        Assert.Empty(planted);
        Assert.Equal(SslPolicyErrors.None, errors);
        Assert.NotEqual(leafSubject, leafIssuer);
        Assert.DoesNotContain(RealAiHostGuard.ProviderHosts, h => root.Contains(h, StringComparison.OrdinalIgnoreCase));
    }
}
