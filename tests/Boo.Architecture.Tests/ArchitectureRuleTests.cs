using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mono.Cecil;
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

    // 002 is an ALLOWLIST: Core may use only the framework types (and, for Path, the members) approved below; anything
    // else fails and the message names it. Disk access is spread across the whole framework (File, StreamWriter(path),
    // Path.Exists, XDocument.Load(path), TarFile, X509 loaders, ...), so a list of bad types or namespaces always misses
    // one; an allowlist of what Core needs cannot. Approving a new type is a one-line reviewed oracle revision.
    // 003 is a DENY rule (the app is the UI and uses far too much of WPF and Windows for an allowlist): every network
    // namespace, the browser controls, Windows Runtime outside the two areas the app uses, and native calls outside the
    // window DLLs. Its known gaps are named in docs/quality/RULES.md. CecilScan does both scans.

    /// <summary>002: framework namespaces Core may use in full: text, collections, maths, JSON, regex, tasks. Exact
    /// names, not their sub-namespaces, so a new namespace needs review.</summary>
    public static readonly string[] CoreApprovedNamespaces =
    [
        "System.Buffers", "System.Buffers.Binary", "System.Collections", "System.Collections.Frozen",
        "System.Collections.Generic", "System.Collections.Immutable", "System.Collections.ObjectModel",
        "System.Diagnostics.CodeAnalysis", "System.Globalization", "System.Linq", "System.Numerics",
        "System.Runtime.CompilerServices", "System.Runtime.Versioning", "System.Text", "System.Text.Json",
        "System.Text.Json.Serialization", "System.Text.RegularExpressions", "System.Threading", "System.Threading.Channels",
        "System.Threading.Tasks",
    ];

    /// <summary>002: the framework assemblies an approval can come from. A namespace or type name is only trusted when
    /// the type really comes from one of these (a package could declare types in any namespace it likes; NuGet reserves
    /// the System.* package names for Microsoft).</summary>
    public static readonly string[] CoreApprovedAssemblies =
    [
        "System.Runtime", "System.Collections", "System.Collections.Concurrent", "System.Collections.Immutable", "System.Linq",
        "System.Memory", "System.ObjectModel", "System.Runtime.InteropServices", "System.Runtime.Intrinsics",
        "System.Runtime.Numerics", "System.Security.Cryptography", "System.Text.Encoding.Extensions", "System.Text.Json",
        "System.Text.RegularExpressions", "System.Threading", "System.Threading.Channels", "System.Net.Http",
        "System.Net.Primitives",
    ];

    /// <summary>002: types inside an approved namespace that are still not allowed: raw memory access, which can reach
    /// any code without naming it.</summary>
    public static readonly string[] CoreDeniedTypes = ["System.Runtime.CompilerServices.Unsafe"];

    /// <summary>The one type that may use CoreDeniedTypes: written by the compiler for inline arrays and collection
    /// literals (Unsafe.Add / As / AsRef); C# code cannot name it or add to it.</summary>
    private const string CompilerHelperType = "<PrivateImplementationDetails>";

    /// <summary>002: single framework types Core may use from namespaces that also hold disk, network or process APIs.
    /// The test for every approval: nothing in the type may depend on or change the files on this machine, or find code
    /// by its name. A type where some members fail that test is approved per member instead, in CoreApprovedMembers.</summary>
    public static readonly string[] CoreApprovedTypes =
    [
        // System: values, primitives, delegates, exceptions, maths, time, Uri as text, runtime facts.
        "System.AccessViolationException", "System.Action", "System.Action`1", "System.Action`2", "System.Action`3",
        "System.AggregateException", "System.ArgumentException", "System.ArgumentNullException",
        "System.ArgumentOutOfRangeException", "System.Array", "System.BadImageFormatException", "System.Boolean", "System.Byte",
        "System.Char", "System.Convert", "System.DateTime", "System.DateTimeKind", "System.DateTimeOffset", "System.Decimal",
        "System.Double", "System.Enum", "System.EventArgs", "System.EventHandler",
        "System.EventHandler`1", "System.Exception", "System.FlagsAttribute", "System.FormatException", "System.Func`1",
        "System.Func`2", "System.Func`3", "System.Func`4", "System.Guid", "System.HashCode", "System.IAsyncDisposable",
        "System.IComparable`1", "System.IDisposable", "System.IEquatable`1", "System.IFormatProvider", "System.Index",
        "System.InsufficientExecutionStackException", "System.Int16", "System.Int32", "System.Int64",
        "System.InvalidOperationException", "System.InvalidProgramException", "System.Lazy`1", "System.Math",
        "System.MemoryExtensions", "System.Memory`1", "System.MidpointRounding", "System.MulticastDelegate",
        "System.NotImplementedException", "System.NotSupportedException", "System.Nullable`1", "System.Object",
        "System.ObjectDisposedException", "System.ObsoleteAttribute", "System.OperatingSystem", "System.OperationCanceledException",
        "System.OutOfMemoryException", "System.OverflowException", "System.ParamArrayAttribute", "System.Predicate`1",
        "System.Random", "System.Range", "System.ReadOnlyMemory`1", "System.ReadOnlySpan`1", "System.Single", "System.Span`1", "System.StackOverflowException", "System.String",
        "System.StringComparer", "System.StringComparison", "System.StringSplitOptions", "System.TimeoutException",
        "System.TimeProvider", "System.TimeSpan", "System.TypeInitializationException", "System.UInt16",
        "System.UInt32", "System.UInt64", "System.UnauthorizedAccessException", "System.Uri", "System.UriKind",
        "System.ValueTuple`2", "System.ValueTuple`3", "System.ValueTuple`4", "System.ValueType", "System.Version", "System.Void",
        // System.Diagnostics: debugger attributes and timing only (no Process, no trace listeners).
        "System.Diagnostics.DebuggableAttribute", "System.Diagnostics.DebuggableAttribute/DebuggingModes",
        "System.Diagnostics.DebuggerHiddenAttribute", "System.Diagnostics.DebuggerDisplayAttribute",
        "System.Diagnostics.DebuggerBrowsableAttribute", "System.Diagnostics.DebuggerBrowsableState",
        "System.Diagnostics.DebuggerStepThroughAttribute", "System.Diagnostics.DebuggerNonUserCodeAttribute",
        "System.Diagnostics.Stopwatch", "System.Diagnostics.UnreachableException",
        // System.IO: exceptions, the attributes enum and in-memory streams (File and Directory are not approved).
        "System.IO.IOException", "System.IO.FileNotFoundException", "System.IO.DirectoryNotFoundException",
        "System.IO.PathTooLongException", "System.IO.EndOfStreamException", "System.IO.InvalidDataException",
        "System.IO.FileAttributes", "System.IO.Stream", "System.IO.MemoryStream", "System.IO.StringReader",
        "System.IO.StringWriter", "System.IO.TextReader", "System.IO.TextWriter", "System.IO.SeekOrigin",
        // System.Net / System.Net.Http: status codes and the error a provider reports (001 counts the Http ones).
        "System.Net.HttpStatusCode", "System.Net.WebUtility", "System.Net.Http.HttpRequestException", "System.Net.Http.HttpRequestError",
        // System.Reflection: the assembly attributes the compiler writes.
        "System.Reflection.AssemblyCompanyAttribute", "System.Reflection.AssemblyConfigurationAttribute",
        "System.Reflection.AssemblyFileVersionAttribute", "System.Reflection.AssemblyInformationalVersionAttribute",
        "System.Reflection.AssemblyProductAttribute", "System.Reflection.AssemblyTitleAttribute",
        "System.Reflection.DefaultMemberAttribute",
        // System.Runtime.InteropServices: span helpers and attributes only (no DllImport, no Marshal).
        "System.Runtime.InteropServices.InAttribute", "System.Runtime.InteropServices.CollectionsMarshal",
        "System.Runtime.InteropServices.SEHException",
        // The attribute the regex source generator writes (the rest of System.CodeDom.Compiler is not approved).
        "System.CodeDom.Compiler.GeneratedCodeAttribute",
        // System.Security.Cryptography: hashing and constant-time compare (no certificate or key-file loaders).
        "System.Security.Cryptography.HashAlgorithmName",
    ];

    /// <summary>002: types approved member by member, because other members of the same type fail the test above:
    /// Path.Exists / GetTempFileName / GetFullPath (disk), Environment.CurrentDirectory / GetLogicalDrives (disk),
    /// Type.GetType(name), Delegate.CreateDelegate(name), SHA256.Create(name) (find code by its name). Only the members listed may be called; the type
    /// itself may be named (a parameter, a field, typeof).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> CoreApprovedMembers = new Dictionary<string, string[]>
    {
        // Members that work on the text of a path only. Not GetFullPath / GetRelativePath: they depend on the current
        // folder and expand short names like PROGRA~1 by asking Windows about the real files (GetLongPathNameW).
        ["System.IO.Path"] =
        [
            "Combine", "Join", "GetFileName", "GetFileNameWithoutExtension", "GetExtension", "HasExtension", "ChangeExtension",
            "GetDirectoryName", "GetPathRoot", "IsPathRooted", "IsPathFullyQualified",
            "TrimEndingDirectorySeparator", "EndsInDirectorySeparator", "GetInvalidPathChars", "GetInvalidFileNameChars",
            "DirectorySeparatorChar", "AltDirectorySeparatorChar", "PathSeparator", "VolumeSeparatorChar",
        ],
        ["System.Environment"] =
        [
            "GetEnvironmentVariable", "get_OSVersion", "get_Version", "get_NewLine", "get_ProcessorCount", "get_TickCount64",
            "get_Is64BitProcess", "get_CurrentManagedThreadId",
        ],
        ["System.Type"] =
        [
            "GetTypeFromHandle", "get_FullName", "get_Namespace", "get_IsEnum", "get_IsValueType", "IsAssignableFrom",
            "op_Equality", "op_Inequality", "Equals", "GetHashCode", "ToString",
        ],
        ["System.Delegate"] = ["Combine", "Remove", "op_Equality", "op_Inequality"],
        ["System.AppContext"] = ["GetData", "TryGetSwitch", "get_BaseDirectory", "get_TargetFrameworkName"],
        // Not Create(string name): it builds any type by its name (CryptoConfig.CreateFromName).
        ["System.Security.Cryptography.SHA256"] = ["HashData"],
        ["System.Security.Cryptography.RandomNumberGenerator"] = ["GetBytes", "Fill", "GetInt32", "GetHexString", "GetString"],
        ["System.Security.Cryptography.CryptographicOperations"] = ["FixedTimeEquals", "ZeroMemory"],
        // Raw handles and memory: may be named (typeof, array initialisers), but nothing that turns them into a pointer.
        ["System.RuntimeTypeHandle"] = [],
        ["System.RuntimeFieldHandle"] = [],
        ["System.Runtime.InteropServices.MemoryMarshal"] = ["CreateReadOnlySpan"],
    };

    private static bool CoreMayUse(TypeReference type)
    {
        // Own code: anything defined in Core itself (including compiler-generated regex classes, which use a System.*
        // namespace). Never trusted by namespace name: a type from any other assembly, Boo.* included, must be approved.
        // Built-in primitives (int, IntPtr, string, object, void) are fine.
        if (type.Scope is ModuleDefinition || type.IsPrimitive) return true;
        if (type.MetadataType is MetadataType.Void or MetadataType.String or MetadataType.Object or MetadataType.TypedByReference) return true;
        if (type.Scope is not AssemblyNameReference from || !CoreApprovedAssemblies.Contains(from.Name)) return false;
        if (CoreDeniedTypes.Contains(type.FullName)) return false;
        var ns = CecilScan.NamespaceOf(type);
        return CoreApprovedNamespaces.Contains(ns) || CoreApprovedTypes.Contains(type.FullName) || CoreApprovedMembers.ContainsKey(type.FullName);
    }

    private static bool CoreMayNotCall(MemberReference member) =>
        CoreApprovedMembers.TryGetValue(CecilScan.OpenName(member.DeclaringType), out var members) && !members.Contains(member.Name);

    /// <summary>003: namespaces whose types talk to the network (System.Net.* includes Http, Sockets, Security,
    /// WebSockets, Quic, Mail, NetworkInformation) and the WebView2 browser.</summary>
    public static readonly string[] NetworkNamespaces = ["System.Net", "Microsoft.Web.WebView2"];

    /// <summary>003: WPF controls that load web pages.</summary>
    public static readonly string[] NetworkTypesElsewhere =
    [
        "System.Windows.Controls.WebBrowser", "System.Windows.Controls.Frame", "System.Windows.Navigation.NavigationWindow",
    ];

    /// <summary>003: the only network-namespace types the app may use: status codes, errors, text helpers.</summary>
    public static readonly string[] HarmlessNetTypes =
    [
        "System.Net.HttpStatusCode", "System.Net.WebUtility", "System.Net.Http.HttpRequestException",
        "System.Net.Http.HttpRequestError", "System.Net.WebSockets.WebSocketException",
        // Used only to read a pasted password out of a PasswordBox (MainWindow.Updates.cs, MainWindow.xaml.cs); no I/O.
        "System.Net.NetworkCredential",
    ];

    /// <summary>003: the only Windows Runtime areas the app may use (today: the async types and the UI settings for
    /// accessibility). Any other Windows.* namespace (Networking, Web, Storage, Security.Authentication.Web, ...) counts.</summary>
    public static readonly string[] AppWinRtNamespaces = ["Windows.Foundation", "Windows.UI.ViewManagement"];

    /// <summary>003: the only native DLLs the app may call: windows, input, monitors, DPI. Anything else (ws2_32,
    /// winhttp, wininet, ...) counts.</summary>
    public static readonly string[] AppNativeDlls = ["user32.dll", "kernel32.dll", "shcore.dll", "dwmapi.dll", "gdi32.dll"];

    private static bool AppNetwork(TypeReference type)
    {
        var ns = CecilScan.NamespaceOf(type);
        if (NetworkTypesElsewhere.Contains(type.FullName)) return true;
        if (CecilScan.InNamespace(ns, NetworkNamespaces)) return !HarmlessNetTypes.Contains(type.FullName);
        return (ns == "Windows" || ns.StartsWith("Windows.", StringComparison.Ordinal)) && !CecilScan.InNamespace(ns, AppWinRtNamespaces);
    }

    [Fact]
    public void BOO_ARCH_002_Core_does_not_touch_the_file_system()
    {
        var scan = CecilScan.TypesReferencing(Core, t => !CoreMayUse(t), CoreMayNotCall)
            .Where(kv => !(kv.Key == CompilerHelperType && kv.Value.All(CoreDeniedTypes.Contains)))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        var native = CecilScan.TypesCallingNative(Core, _ => true, countComImports: true).ToList(); // no native, COM, by-name or raw-address calls
        if (CecilScan.AllowsUnsafeCode(Core)) native.Add("<Boo.Core compiled with unsafe code allowed>");
        // ScopedFiles is the second class in ActionPolicy.cs (the plan's "ActionPolicy.cs:209"). StartupRegistration is a
        // third the plan's text count could not see: Path.GetFullPath (StartupRegistration.cs:68-69) asks Windows for the
        // long form of short names like RUNNER~1, so its answer depends on the files on the machine.
        AssertExactOffenders("BOO-ARCH-002", scan.Keys.Concat(native).Distinct().ToList(),
            ["Boo.Core.FileActionResolver", "Boo.Core.ScopedFiles", "Boo.Core.StartupRegistration"], Detail(scan, native, "not approved for Core"));
    }

    [Fact]
    public void BOO_ARCH_003_App_does_not_create_network_clients()
    {
        var scan = CecilScan.TypesReferencing(App, AppNetwork);
        var native = CecilScan.TypesCallingNative(App, dll => !AppNativeDlls.Contains(NativeDllName(dll), StringComparer.OrdinalIgnoreCase), countComImports: false);
        // MainWindow: the _http field and its two `new HttpClient` (the plan's 3 references, MainWindow.xaml.cs:65,1624,2123).
        // Not counted on purpose: Velopack's update check and download (UpdateService, release lane), not a chat client.
        AssertExactOffenders("BOO-ARCH-003", scan.Keys.Concat(native).Distinct().ToList(), ["Boo.App.MainWindow"],
            Detail(scan, native, "network"));
    }

    private static string NativeDllName(string module) =>
        module.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? module : module + ".dll";

    private static string Detail(IReadOnlyDictionary<string, SortedSet<string>> scan, IReadOnlyCollection<string> native, string what) =>
        string.Join("; ", scan.Select(kv => $"{kv.Key} uses ({what}): {string.Join(", ", kv.Value)}")
            .Concat(native.Select(n => $"{n}: native or COM call")));

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
    public const int WindowCodeBehindLineBudget = 2679; // measured on lane/face 2433397 (PR #33 added 5 lines to MainWindow.xaml.cs)

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

    private static void AssertExactOffenders(string rule, IReadOnlyCollection<string> actual, IReadOnlyCollection<string> allowed, string detail = "")
    {
        var added = actual.Except(allowed).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var fixedNow = allowed.Except(actual).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var anchor = rule.ToLowerInvariant();
        Assert.True(added.Count == 0,
            $"{rule}: new offender(s): {string.Join(", ", added)}. See {RulesDoc}#{anchor} for the fix. " +
            (detail.Length > 0 ? $"What was found: {detail}. " : "") +
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
