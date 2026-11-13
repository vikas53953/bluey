using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using NetArchTest.Rules;

namespace Boo.Architecture.Tests;

/// <summary>
/// The architecture rules of docs/plans/agent-trust-plan.md section 2.4 B, texts and fixes in docs/quality/RULES.md.
/// Each rule lists today's offenders by name (baselined 13 Nov 2026 on lane/face af089dd). The list is exact both ways:
/// a new offender fails ("do not add one"), and an offender that has been fixed also fails until it is removed from
/// the list, so every improvement is locked in. Removing a name edits this frozen file, so it goes through a reviewed
/// oracle revision (scripts/test_oracle.py --rebaseline). Dependency offenders (001-004) are reported per outermost
/// type: compiler-made nested types (lambdas, iterators, async state machines) count towards the type that wrote the
/// code. Static-state offenders (005) are reported per declaring type, nested types included (Outer+Inner.Field).
/// </summary>
public sealed class ArchitectureRuleTests
{
    private const string RulesDoc = "docs/quality/RULES.md";

    private static readonly Assembly Core = Assembly.Load("Boo.Core");
    private static readonly Assembly Providers = Assembly.Load("Boo.Providers");
    private static readonly Assembly Storage = Assembly.Load("Boo.Storage");
    private static readonly Assembly Windows = Assembly.Load("Boo.Windows");
    private static readonly Assembly App = Assembly.Load("Boo"); // src/Boo.App builds Boo.exe / Boo.dll
    private static readonly Assembly[] Src = [Core, Providers, Storage, Windows, App];

    [Fact]
    public void BOO_ARCH_001_Core_has_no_network_registry_database_audio_or_installer_dependencies()
    {
        var result = Types.InAssembly(Core).ShouldNot().HaveDependencyOnAny(
            "System.Net.Http", "System.Net.WebSockets", "Microsoft.Win32", "Microsoft.Data.Sqlite", "NAudio", "Velopack")
            .GetResult();
        AssertExactOffenders("BOO-ARCH-001", Offenders(result), ["Boo.Core.ProviderFailureClassifier"]);
    }

    // 002 and 003 are DEFAULT-DENY: every type in the disk namespaces (002) or the network namespaces (003) counts, except
    // a short list of harmless value / in-memory types. A disk or network API that nobody thought of is therefore caught
    // (StreamWriter(path), ZipFile, TcpListener, SslStream, Dns, ...); a harmless type missing from the list fails
    // loudly instead of slipping through silently. Native calls (DllImport) are covered too. CecilScan does the scan.

    /// <summary>002: namespaces whose types read or write files (System.IO.* includes Compression, MemoryMappedFiles,
    /// Pipes, IsolatedStorage; System.Xml.* includes the loaders that take a path).</summary>
    public static readonly string[] DiskNamespaces = ["System.IO", "System.Xml"];

    /// <summary>002: types outside those namespaces that read the disk or start a program that can.</summary>
    public static readonly string[] DiskTypesElsewhere =
    [
        "System.Diagnostics.Process", "System.Diagnostics.ProcessStartInfo", "System.Diagnostics.FileVersionInfo",
    ];

    /// <summary>002: the only disk-namespace types Core may use: paths as text, in-memory streams, enums, exceptions.</summary>
    public static readonly string[] HarmlessIoTypes =
    [
        "System.IO.Path", "System.IO.FileAttributes", "System.IO.SearchOption", "System.IO.SeekOrigin",
        "System.IO.Stream", "System.IO.MemoryStream", "System.IO.StringReader", "System.IO.StringWriter",
        "System.IO.TextReader", "System.IO.TextWriter", "System.IO.BinaryReader", "System.IO.BinaryWriter",
        "System.IO.IOException", "System.IO.FileNotFoundException", "System.IO.DirectoryNotFoundException",
        "System.IO.PathTooLongException", "System.IO.EndOfStreamException", "System.IO.InvalidDataException",
    ];

    /// <summary>002: members of a harmless type that still touch the disk.</summary>
    public static readonly string[] DiskMembersOfHarmlessTypes = ["System.IO.Path::GetTempFileName"];

    /// <summary>003: namespaces whose types talk to the network (System.Net.* includes Http, Sockets, Security,
    /// WebSockets, Quic, Mail, NetworkInformation; plus the WinRT and WebView2 network surfaces).</summary>
    public static readonly string[] NetworkNamespaces = ["System.Net", "Windows.Networking", "Windows.Web", "Microsoft.Web.WebView2"];

    /// <summary>003: the only network-namespace types the app may use: status codes, errors, text helpers.</summary>
    public static readonly string[] HarmlessNetTypes =
    [
        "System.Net.HttpStatusCode", "System.Net.WebUtility", "System.Net.Http.HttpRequestException",
        "System.Net.Http.HttpRequestError", "System.Net.WebSockets.WebSocketException",
        // Used only to read a pasted password out of a PasswordBox (MainWindow.Updates.cs, MainWindow.xaml.cs); no I/O.
        "System.Net.NetworkCredential",
    ];

    /// <summary>003: the only native DLLs the app may call: windows, input, monitors, DPI. Anything else (ws2_32,
    /// winhttp, wininet, ...) counts as a network client.</summary>
    public static readonly string[] AppNativeDlls = ["user32.dll", "kernel32.dll", "shcore.dll", "dwmapi.dll", "gdi32.dll"];

    [Fact]
    public void BOO_ARCH_002_Core_does_not_touch_the_file_system()
    {
        var offenders = CecilScan.TypesReferencing(Core,
            t => DiskTypesElsewhere.Contains(t.FullName)
                 || (CecilScan.InNamespace(CecilScan.NamespaceOf(t), DiskNamespaces) && !HarmlessIoTypes.Contains(t.FullName)),
            m => DiskMembersOfHarmlessTypes.Contains($"{m.DeclaringType.FullName}::{m.Name}"));
        var native = CecilScan.TypesCallingNative(Core, _ => true); // Core makes no native calls at all
        // ScopedFiles is the second class in ActionPolicy.cs (the plan's "ActionPolicy.cs:209").
        AssertExactOffenders("BOO-ARCH-002", offenders.Concat(native).Distinct().ToList(), ["Boo.Core.FileActionResolver", "Boo.Core.ScopedFiles"]);
    }

    [Fact]
    public void BOO_ARCH_003_App_does_not_create_network_clients()
    {
        var offenders = CecilScan.TypesReferencing(App,
            t => CecilScan.InNamespace(CecilScan.NamespaceOf(t), NetworkNamespaces) && !HarmlessNetTypes.Contains(t.FullName));
        var native = CecilScan.TypesCallingNative(App, dll => !AppNativeDlls.Contains(NativeDllName(dll), StringComparer.OrdinalIgnoreCase));
        // MainWindow: the _http field and its two `new HttpClient` (the plan's 3 references, MainWindow.xaml.cs:65,1624,2123).
        // Not counted on purpose: Velopack's update download (UpdateService), owned by the release lane, not a chat client.
        AssertExactOffenders("BOO-ARCH-003", offenders.Concat(native).Distinct().ToList(), ["Boo.App.MainWindow"]);
    }

    private static string NativeDllName(string module) =>
        module.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? module : module + ".dll";

    [Fact]
    public void BOO_ARCH_004_Windows_layer_has_no_WPF_controls_shapes_or_windows()
    {
        var dependsOnControls = Types.InAssembly(Windows).ShouldNot().HaveDependencyOnAny("System.Windows.Controls", "System.Windows.Shapes")
            .GetResult();
        var derivesFromWindow = Types.InAssembly(Windows).ShouldNot().Inherit(typeof(System.Windows.Window)).GetResult();
        AssertExactOffenders("BOO-ARCH-004", Offenders(dependsOnControls).Concat(Offenders(derivesFromWindow)).Distinct().ToList(), []);
    }

    [Fact]
    public void BOO_ARCH_005_No_mutable_static_state_in_src()
    {
        // A static field that is neither readonly nor const, or a static property with a setter (an auto-property's
        // setter shows up as its writable backing field). Compiler-made caches (lambda singletons) are not counted.
        // Named with the full nested type name (Outer+Inner), so a new static in a nested class can never hide behind
        // an allowlisted one with the same name. Known gap (docs/quality/RULES.md): a readonly field holding a mutable
        // collection that is changed later (static readonly List<T> + Add) is shared state this rule does not see.
        var offenders = new List<string>();
        foreach (var assembly in Src)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (IsCompilerGenerated(type)) continue;
                const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                foreach (var field in type.GetFields(Statics))
                {
                    if (field.IsInitOnly || field.IsLiteral) continue;
                    var member = BackingFieldProperty(field.Name) ?? field.Name;
                    offenders.Add($"{type.FullName}.{member}");
                }
                foreach (var property in type.GetProperties(Statics))
                {
                    if (property.SetMethod is null) continue;
                    offenders.Add($"{type.FullName}.{property.Name}");
                }
            }
        }
        AssertExactOffenders("BOO-ARCH-005", offenders.Distinct().ToList(),
            ["Boo.App.AccessibilitySettings._uiSettings", "Boo.App.App.StartedInBackground"]);
    }

    /// <summary>Today's largest window code-behind file (MainWindow.xaml.cs). The budget may only go down.</summary>
    public const int WindowCodeBehindLineBudget = 2674;

    [Fact]
    public void BOO_ARCH_006_Window_code_behind_files_stay_within_the_line_budget()
    {
        var appDirectory = Path.Combine(RepoRoot(), "src", "Boo.App");
        var files = Directory.GetFiles(appDirectory, "*Window*.cs", SearchOption.TopDirectoryOnly);
        Assert.NotEmpty(files);
        var over = files
            .Select(f => (Name: Path.GetFileName(f), Lines: File.ReadAllLines(f).Length))
            .Where(f => f.Lines > WindowCodeBehindLineBudget)
            .Select(f => $"{f.Name}: {f.Lines} lines")
            .ToList();
        Assert.True(over.Count == 0,
            $"BOO-ARCH-006: window code-behind over the {WindowCodeBehindLineBudget}-line budget: {string.Join(", ", over)}. " +
            $"Fix: move the feature into its own MainWindow.<Feature>.cs partial (BOO-ARCH-007) or into Boo.Core. " +
            $"Do NOT raise the budget. Rule: {RulesDoc}#boo-arch-006");
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static List<string> Offenders(TestResult result) =>
        result.IsSuccessful || result.FailingTypes is null
            ? []
            : result.FailingTypes.Select(OutermostName).Distinct().ToList();

    private static string OutermostName(Type type)
    {
        var outer = type;
        while (outer.DeclaringType is not null) outer = outer.DeclaringType;
        return outer.FullName ?? outer.Name;
    }

    private static bool IsCompilerGenerated(Type type)
    {
        for (var t = type; t is not null; t = t.DeclaringType)
        {
            if (t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)) return true;
        }
        return false;
    }

    /// <summary>"&lt;Name&gt;k__BackingField" -> "Name" (an auto-property with a setter), otherwise null.</summary>
    private static string? BackingFieldProperty(string fieldName) =>
        fieldName.StartsWith('<') && fieldName.EndsWith(">k__BackingField", StringComparison.Ordinal)
            ? fieldName[1..fieldName.IndexOf('>', StringComparison.Ordinal)]
            : null;

    private static void AssertExactOffenders(string rule, IReadOnlyCollection<string> actual, IReadOnlyCollection<string> allowed)
    {
        var added = actual.Except(allowed).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var fixedNow = allowed.Except(actual).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var anchor = rule.ToLowerInvariant();
        Assert.True(added.Count == 0,
            $"{rule}: new offender(s): {string.Join(", ", added)}. See {RulesDoc}#{anchor} for the fix. " +
            "Do NOT add the name to the allowlist; the allowlist only shrinks.");
        Assert.True(fixedNow.Count == 0,
            $"{rule}: no longer an offender (good): {string.Join(", ", fixedNow)}. Remove it from this rule's allowlist " +
            "in a reviewed oracle revision (scripts/test_oracle.py --rebaseline) so the gain is locked in.");
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Boo.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Boo.sln not found above " + AppContext.BaseDirectory);
    }
}
