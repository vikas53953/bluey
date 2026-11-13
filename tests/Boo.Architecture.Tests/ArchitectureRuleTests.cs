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
/// oracle revision (scripts/test_oracle.py --rebaseline). Offenders are reported per outermost type: compiler-made
/// nested types (lambdas, iterators, async state machines) count towards the type that wrote the code.
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

    // 002 and 003 name exact types, so they match exact types (CecilScan): NetArchTest matches by name prefix, and
    // "System.IO.File" would also catch FileAttributes or FileNotFoundException, which touch no file.

    [Fact]
    public void BOO_ARCH_002_Core_does_not_touch_the_file_system()
    {
        var offenders = CecilScan.TypesReferencing(Core, "System.IO.File", "System.IO.Directory");
        // ScopedFiles is the second class in ActionPolicy.cs (the plan's "ActionPolicy.cs:209").
        AssertExactOffenders("BOO-ARCH-002", offenders, ["Boo.Core.FileActionResolver", "Boo.Core.ScopedFiles"]);
    }

    [Fact]
    public void BOO_ARCH_003_App_does_not_create_network_clients()
    {
        var offenders = CecilScan.TypesReferencing(App, "System.Net.Http.HttpClient", "System.Net.WebSockets.ClientWebSocket");
        // MainWindow: the _http field and its two `new HttpClient` (the plan's 3 references, MainWindow.xaml.cs:65,1624,2123).
        AssertExactOffenders("BOO-ARCH-003", offenders, ["Boo.App.MainWindow"]);
    }

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
                    offenders.Add($"{OutermostName(type)}.{member}");
                }
                foreach (var property in type.GetProperties(Statics))
                {
                    if (property.SetMethod is null) continue;
                    offenders.Add($"{OutermostName(type)}.{property.Name}");
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
