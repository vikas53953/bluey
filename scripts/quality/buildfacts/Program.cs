using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MSTask = Microsoft.Build.Logging.StructuredLogger.Task;
using Metadata = Microsoft.Build.Logging.StructuredLogger.Metadata;

// Build-facts extractor (scripts/quality/build_facts.py checks its output; docs/quality/RULES.md "Build facts").
//
//   BuildFacts --repo <root> --out <build-facts.json> <label>=<binlog> [<label>=<binlog> ...]
//
// Reads the binary logs of the REAL build (windows-ci's Release build and publish) and writes what that build really
// did: for every project / target framework / runtime identifier that was evaluated, its imports (and the imports that
// were skipped, with the reason), its PackageReference / PackageDownload / ProjectReference items and the packages
// restored into obj/project.assets.json; for every Csc task that ran, the compiler it ran, the whole command line with
// every response file expanded (and each response file's text), every analyzer config with its text, analyzers,
// additional files, nowarn / warnaserror / ruleset / errorlog (with the SARIF log itself), Roslyn's own reading of the
// options, the references with where each came from, and the output assembly's SHA-256; for the whole build, every task
// that ran (name, target, the file that defines the target, the task's assembly), every Copy of an assembly, every
// ReadyToRun compile (input, output, SHA-256) and the runtime packs used.
//
// Paths are written repo-relative (forward slashes) or under a token: <dotnet>/ (the SDK install), <nuget>/ (the NuGet
// package folder), <temp>/, <home>/, so no machine path or user name is written. The environment block of the log is
// never read. Keys are sorted, lists are sorted where order has no meaning (command-line order is kept), so the same
// build gives the same file. Exit 2 when the tool cannot do its job (unreadable log, a machine path or the user name
// left in the output); it never writes a partial file.

if (args is ["--self-test"])
{
    return SelfTest.Run();
}

var options = ParseArgs(args);
if (options is null)
{
    Console.Error.WriteLine("usage: BuildFacts --repo <root> --out <build-facts.json> <label>=<binlog> [...] | BuildFacts --self-test");
    return 2;
}

try
{
    var facts = new JsonObject
    {
        ["schema"] = "boo-build-facts/1",
        ["reader"] = new JsonObject
        {
            ["msbuildStructuredLogger"] = Version(typeof(BinaryLog)),
            ["roslynCommandLineParser"] = Version(typeof(CSharpCommandLineParser)),
        },
    };
    var logs = new JsonArray();
    foreach (var (label, path) in options.Value.Logs)
    {
        logs.Add(new Extractor(options.Value.Repo, label, path).Run());
    }

    facts["binlogs"] = logs;
    var leaks = PathTokens.Leaks(facts, Environment.UserName);
    if (leaks.Count > 0)
    {
        foreach (var leak in leaks.Take(40))
        {
            Console.Error.WriteLine($"refusing to write: machine-specific text left in the facts at {leak}");
        }

        Console.Error.WriteLine($"{leaks.Count} value(s) still hold a machine path; add the root's property to "
            + "PathTokens.FromProperties (never widen the guard)");
        return 2;
    }

    var text = Json.Write(facts);

    File.WriteAllText(options.Value.Out, text, new UTF8Encoding(false));
    Console.WriteLine($"wrote {options.Value.Out} ({text.Length} chars, {logs.Count} binary logs)");
    return 0;
}
catch (FactsException e)
{
    Console.Error.WriteLine($"build facts: {e.Message}");
    return 2;
}

static string? Version(Type type) =>
    (type.Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion.Split('+')[0];

static (string Repo, string Out, List<(string Label, string Path)> Logs)? ParseArgs(string[] args)
{
    string? repo = null, output = null;
    var logs = new List<(string, string)>();
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--repo" && i + 1 < args.Length)
        {
            repo = Path.GetFullPath(args[++i]);
        }
        else if (args[i] == "--out" && i + 1 < args.Length)
        {
            output = Path.GetFullPath(args[++i]);
        }
        else if (args[i].Contains('=') && !args[i].StartsWith('-'))
        {
            var eq = args[i].IndexOf('=');
            logs.Add((args[i][..eq], Path.GetFullPath(args[i][(eq + 1)..])));
        }
        else
        {
            return null;
        }
    }

    if (repo is null || output is null || logs.Count == 0 || logs.Select(l => l.Item1).Distinct().Count() != logs.Count)
    {
        return null;
    }

    return (repo, output, logs);
}

sealed class FactsException(string message) : Exception(message);

sealed class Extractor
{
    static readonly string[] UnitProperties =
    [
        "AssemblyName", "OutputType", "TargetFramework", "RuntimeIdentifier", "Configuration", "Platform", "SelfContained",
        "_IsPublishing", "SolutionName", "ErrorLog", "TreatWarningsAsErrors", "WarningsAsErrors", "WarningsNotAsErrors",
        "NoWarn", "CodeAnalysisRuleSet", "RunAnalyzers", "RunAnalyzersDuringBuild", "EnforceCodeStyleInBuild",
        "AnalysisLevel", "Deterministic", "PublishReadyToRun", "UseWPF", "UseWindowsForms", "CscToolPath", "CscToolExe",
        "DOTNET_HOST_PATH", "UseSharedCompilation", "ProduceReferenceAssembly", "LangVersion", "Nullable",
        "IsPublishable", "IntermediateOutputPath", "OutputPath", "MSBuildProjectExtensionsPath",
    ];

    readonly string repo;
    readonly string label;
    readonly string path;
    readonly PathTokens tokens = new();
    Build build = null!;

    public Extractor(string repo, string label, string path)
    {
        this.repo = repo;
        this.label = label;
        this.path = path;
    }

    public JsonObject Run()
    {
        if (!File.Exists(path))
        {
            throw new FactsException($"binary log not found: {label}");
        }

        build = BinaryLog.ReadBuild(path);
        var evaluations = build.FindChildrenRecursive<ProjectEvaluation>().ToList();
        SetRoots(evaluations);

        var units = new SortedDictionary<string, Unit>(StringComparer.Ordinal);
        foreach (var project in build.FindChildrenRecursive<Project>())
        {
            if (project.ProjectFile is null || project.ProjectFile.EndsWith(".metaproj", StringComparison.OrdinalIgnoreCase)
                || project.ProjectFile.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || project.ProjectFile.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var evaluation = build.FindEvaluation(project.EvaluationId);
            var props = evaluation is null ? new Dictionary<string, string>() : Properties(evaluation);
            var unitKey = string.Join("|", Norm(project.ProjectFile), props.GetValueOrDefault("TargetFramework", ""),
                props.GetValueOrDefault("RuntimeIdentifier", ""), props.GetValueOrDefault("Configuration", ""));
            if (!units.TryGetValue(unitKey, out var unit))
            {
                unit = new Unit(project.ProjectFile, props);
                units[unitKey] = unit;
            }

            if (evaluation is not null)
            {
                unit.Evaluations.Add(evaluation);
            }

            unit.Projects.Add(project);
        }

        var unitArray = new JsonArray();
        foreach (var unit in units.Values)
        {
            unitArray.Add(UnitFacts(unit));
        }

        var commandLine = build.FindChildrenRecursive<Message>()
            .Select(m => m.Text)
            .FirstOrDefault(t => t is not null && t.StartsWith("Command line arguments = ", StringComparison.Ordinal));

        return new JsonObject
        {
            ["label"] = label,
            ["succeeded"] = build.Succeeded,
            ["msbuildVersion"] = build.MSBuildVersion,
            ["commandLine"] = commandLine is null ? null : CommandLine(commandLine["Command line arguments = ".Length..].Trim('"')),
            ["units"] = unitArray,
            ["tasks"] = TaskCatalog(),
            ["copies"] = Copies(),
            ["readyToRun"] = ReadyToRun(),
            ["runtimePacks"] = RuntimePacks(),
        };
    }

    // The MSBuild command line, split, with property values filtered (PropertyFilter) and paths tokenised.
    string CommandLine(string text) => string.Join(" ",
        CommandLineParser.SplitCommandLineIntoArguments(text, removeHashComments: false)
            .Select(arg => Norm(PropertyFilter.Argument(arg)).Replace('\\', '/')));

    sealed class Unit(string projectFile, Dictionary<string, string> props)
    {
        public string ProjectFile { get; } = projectFile;
        public Dictionary<string, string> Props { get; } = props;
        public HashSet<ProjectEvaluation> Evaluations { get; } = [];
        public List<Project> Projects { get; } = [];
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Path normalisation
    // -----------------------------------------------------------------------------------------------------------------
    // Every machine root this build used, from its own evaluated properties (see PathTokens), then this process's.
    void SetRoots(List<ProjectEvaluation> evaluations)
    {
        tokens.AddRepo(repo);
        if (build.MSBuildExecutablePath is { Length: > 0 } msbuild)
        {
            // <dotnet>/sdk/<version>/MSBuild.dll
            tokens.Add(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(msbuild))), "<dotnet>/");
        }

        foreach (var evaluation in evaluations)
        {
            tokens.AddFromProperties(Properties(evaluation));
        }

        tokens.Add(Path.GetTempPath(), "<temp>/");
        tokens.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "<programfiles>/");
        tokens.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "<programfiles-x86>/");
        tokens.Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "<windows>/");
        tokens.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "<programdata>/");
        tokens.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "<localappdata>/");
        tokens.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<home>/");
    }

    string Norm(string? text) => tokens.Norm(text);

    // A path: absolute (relative ones are taken from the given base), normalised, with forward slashes.
    string NormPath(string? value, string? baseDirectory = null)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var full = value;
        try
        {
            full = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(baseDirectory ?? repo, value));
        }
        catch (ArgumentException)
        {
            // keep the text as given
        }

        // A file in a folder ABOVE the repository (MSBuild probes every parent folder for .editorconfig/.globalconfig/
        // Directory.Build.*): keep how far up it is and its name, not the machine's folder names.
        var directory = Path.GetDirectoryName(full);
        var up = 0;
        for (var d = Path.GetDirectoryName(repo); d is not null; d = Path.GetDirectoryName(d))
        {
            up++;
            if (directory is not null && string.Equals(d.TrimEnd('\\', '/'), directory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                return $"<repo-parent-{up}>/" + Path.GetFileName(full);
            }
        }

        return Norm(full).Replace('\\', '/');
    }

    string? Resolve(string? value, string? baseDirectory)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(baseDirectory ?? repo, value));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    static string? Sha256(string? file)
    {
        if (file is null || !File.Exists(file))
        {
            return null;
        }

        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Evaluation
    // -----------------------------------------------------------------------------------------------------------------
    static Dictionary<string, string> Properties(ProjectEvaluation evaluation)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var folder = evaluation.FindChild<Folder>("Properties");
        if (folder is null)
        {
            return result;
        }

        foreach (var property in folder.Children.OfType<Property>())
        {
            result[property.Name] = property.Value ?? "";
        }

        return result;
    }

    static Dictionary<string, string> GlobalProperties(ProjectEvaluation evaluation)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var global = evaluation.FindChild<Folder>("Properties")?.FindChild<Folder>("Global");
        if (global is not null)
        {
            foreach (var property in global.Children.OfType<Property>())
            {
                result[property.Name] = property.Value ?? "";
            }
        }

        return result;
    }

    static IEnumerable<Item> Items(ProjectEvaluation evaluation, string itemType)
    {
        var folder = evaluation.FindChild<Folder>("Items");
        if (folder is null)
        {
            yield break;
        }

        foreach (var group in folder.Children.OfType<NamedNode>().Where(n => n.Name == itemType))
        {
            foreach (var item in group.Children.OfType<Item>())
            {
                yield return item;
            }
        }
    }

    static Dictionary<string, string> Meta(Item item)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (item.HasChildren)
        {
            foreach (var m in item.Children.OfType<Metadata>())
            {
                result[m.Name] = m.Value ?? "";
            }
        }

        return result;
    }

    JsonObject UnitFacts(Unit unit)
    {
        var projectDirectory = Path.GetDirectoryName(unit.ProjectFile)!;
        var imports = new SortedSet<string>(StringComparer.Ordinal);
        var skipped = new SortedSet<string>(StringComparer.Ordinal);
        var packageReferences = new SortedSet<string>(StringComparer.Ordinal);
        var packageDownloads = new SortedSet<string>(StringComparer.Ordinal);
        var projectReferences = new SortedSet<string>(StringComparer.Ordinal);
        var plainReferences = new SortedSet<string>(StringComparer.Ordinal);
        var frameworkReferences = new SortedSet<string>(StringComparer.Ordinal);
        var editorConfigs = new SortedSet<string>(StringComparer.Ordinal);
        var globalSets = new SortedSet<string>(StringComparer.Ordinal);
        string? assetsFile = null;
        foreach (var evaluation in unit.Evaluations)
        {
            foreach (var import in evaluation.FindChildrenRecursive<Import>())
            {
                imports.Add(NormPath(import.ImportedProjectFilePath) + " <= " + NormPath(import.ProjectFilePath));
            }

            foreach (var none in evaluation.FindChildrenRecursive<NoImport>())
            {
                skipped.Add(Norm(none.Text).Replace('\\', '/') + " <= " + NormPath(none.ProjectFilePath)
                    + " (" + Norm(none.Reason) + ")");
            }

            foreach (var item in Items(evaluation, "PackageReference"))
            {
                packageReferences.Add(item.Text + " " + Meta(item).GetValueOrDefault("Version", ""));
            }

            foreach (var item in Items(evaluation, "PackageDownload"))
            {
                packageDownloads.Add(item.Text + " " + Meta(item).GetValueOrDefault("Version", ""));
            }

            foreach (var item in Items(evaluation, "ProjectReference"))
            {
                projectReferences.Add(NormPath(item.Text, projectDirectory));
            }

            foreach (var item in Items(evaluation, "Reference"))
            {
                var hint = Meta(item).GetValueOrDefault("HintPath", "");
                plainReferences.Add(Norm(item.Text) + (hint.Length > 0 ? " hint=" + NormPath(hint, projectDirectory) : ""));
            }

            foreach (var item in Items(evaluation, "FrameworkReference"))
            {
                frameworkReferences.Add(item.Text);
            }

            foreach (var type in new[] { "EditorConfigFiles", "GlobalAnalyzerConfigFiles" })
            {
                foreach (var item in Items(evaluation, type))
                {
                    editorConfigs.Add(type + ": " + NormPath(item.Text, projectDirectory));
                }
            }

            var global = GlobalProperties(evaluation);
            // Only reviewed names keep their values (PropertyFilter), so `-p:Name=secret` never reaches the facts.
            globalSets.Add(string.Join("; ", global
                .Where(p => p.Key is not "CurrentSolutionConfigurationContents" and not "MSBuildRestoreSessionId")
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => PropertyFilter.Pair(p.Key, Norm(p.Value)))));
            assetsFile ??= Properties(evaluation).GetValueOrDefault("ProjectAssetsFile");
        }

        var properties = new JsonObject();
        foreach (var name in UnitProperties)
        {
            if (unit.Props.TryGetValue(name, out var value))
            {
                properties[name] = Norm(value);
            }
        }

        var cscCalls = new JsonArray();
        var followed = new SortedSet<string>(StringComparer.Ordinal);
        var targetsRun = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var project in unit.Projects)
        {
            foreach (var target in project.Children.OfType<Target>())
            {
                if (target.HasChildren && target.Children.OfType<MSTask>().Any())
                {
                    targetsRun.Add(target.Name);
                }
            }

            foreach (var task in project.FindChildrenRecursive<MSTask>().Where(t => t.Name == "Csc"))
            {
                if (task.GetNearestParent<Project>() != project)
                {
                    continue;
                }

                var call = CscFacts(task, projectDirectory);
                foreach (var reference in call["references"]!.AsArray())
                {
                    if (reference!["origin"]!.GetValue<string>() == "projectReference")
                    {
                        followed.Add(reference["project"]!.GetValue<string>());
                    }
                }

                cscCalls.Add(call);
            }
        }

        return new JsonObject
        {
            ["project"] = NormPath(unit.ProjectFile),
            ["targetFramework"] = unit.Props.GetValueOrDefault("TargetFramework", ""),
            ["runtimeIdentifier"] = unit.Props.GetValueOrDefault("RuntimeIdentifier", ""),
            ["configuration"] = unit.Props.GetValueOrDefault("Configuration", ""),
            ["globalProperties"] = Arr(globalSets),
            ["properties"] = properties,
            ["imports"] = Arr(imports),
            ["importsSkipped"] = Arr(skipped),
            ["packageReferences"] = Arr(packageReferences),
            ["packageDownloads"] = Arr(packageDownloads),
            ["projectReferenceItems"] = Arr(projectReferences),
            ["projectReferencesFollowed"] = Arr(followed),
            ["referenceItems"] = Arr(plainReferences),
            ["frameworkReferences"] = Arr(frameworkReferences),
            ["editorConfigItems"] = Arr(editorConfigs),
            ["assets"] = Assets(assetsFile),
            ["targetsWithTasks"] = Arr(targetsRun),
            ["csc"] = cscCalls,
        };
    }

    JsonObject? Assets(string? assetsFile)
    {
        if (assetsFile is null)
        {
            return null;
        }

        var result = new JsonObject { ["path"] = NormPath(assetsFile) };
        if (!File.Exists(assetsFile))
        {
            result["missing"] = true;
            return result;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(assetsFile));
        var libraries = new SortedSet<string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("libraries", out var libs))
        {
            foreach (var lib in libs.EnumerateObject())
            {
                var type = lib.Value.TryGetProperty("type", out var t) ? t.GetString() : "";
                var libPath = lib.Value.TryGetProperty("path", out var p) ? p.GetString() : null;
                libraries.Add(lib.Name + " (" + type + (type == "project" && libPath is not null ? " " + libPath.Replace('\\', '/') : "") + ")");
            }
        }

        var downloads = new SortedSet<string>(StringComparer.Ordinal);
        var direct = new SortedSet<string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("project", out var project)
            && project.TryGetProperty("frameworks", out var frameworks))
        {
            foreach (var framework in frameworks.EnumerateObject())
            {
                if (framework.Value.TryGetProperty("downloadDependencies", out var dd))
                {
                    foreach (var d in dd.EnumerateArray())
                    {
                        downloads.Add(framework.Name + ": " + d.GetProperty("name").GetString() + " " + d.GetProperty("version").GetString());
                    }
                }

                if (framework.Value.TryGetProperty("dependencies", out var deps))
                {
                    foreach (var d in deps.EnumerateObject())
                    {
                        var version = d.Value.TryGetProperty("version", out var v) ? v.GetString() : "";
                        direct.Add(framework.Name + ": " + d.Name + " " + version);
                    }
                }
            }
        }

        result["libraries"] = Arr(libraries);
        result["downloadDependencies"] = Arr(downloads);
        result["directDependencies"] = Arr(direct);
        return result;
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Csc
    // -----------------------------------------------------------------------------------------------------------------
    JsonObject CscFacts(MSTask task, string projectDirectory)
    {
        var target = task.GetNearestParent<Target>();
        var commandLine = task.CommandLineArguments ?? "";
        var all = CommandLineParser.SplitCommandLineIntoArguments(commandLine, removeHashComments: false).ToList();
        var split = all.FindIndex(arg => Path.GetFileName(arg).ToLowerInvariant() is "csc.dll" or "csc.exe" or "csc");
        var tool = split < 0 ? new List<string>() : all.Take(split + 1).ToList();
        var rest = split < 0 ? all : all.Skip(split + 1).ToList();

        var responseFiles = new JsonArray();
        var expanded = new List<string>();
        Expand(rest, projectDirectory, expanded, responseFiles, 0);

        var parsed = CSharpCommandLineParser.Default.Parse(rest, projectDirectory, sdkDirectory: null);
        var compilation = parsed.CompilationOptions;
        var specific = new JsonObject();
        foreach (var pair in compilation.SpecificDiagnosticOptions.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            specific[pair.Key] = pair.Value.ToString();
        }

        var analyzerConfigs = new JsonArray();
        foreach (var config in parsed.AnalyzerConfigPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var full = Resolve(config, projectDirectory);
            analyzerConfigs.Add(new JsonObject
            {
                ["path"] = NormPath(config, projectDirectory),
                ["content"] = full is not null && File.Exists(full) ? Norm(File.ReadAllText(full)) : null,
            });
        }

        string? errorLogPath = parsed.ErrorLogOptions?.Path;
        JsonNode? errorLog = null;
        if (errorLogPath is not null)
        {
            var full = Resolve(errorLogPath, projectDirectory);
            JsonNode? content = null;
            if (full is not null && File.Exists(full))
            {
                var text = Norm(File.ReadAllText(full));
                try
                {
                    content = JsonNode.Parse(text);
                }
                catch (JsonException)
                {
                    content = text;
                }
            }

            errorLog = new JsonObject { ["path"] = NormPath(errorLogPath, projectDirectory), ["sarif"] = content };
        }

        var outputAssembly = Resolve(ParameterValue(task, "OutputAssembly"), projectDirectory)
            ?? (parsed.OutputFileName is null ? null : Path.Combine(parsed.OutputDirectory ?? projectDirectory, parsed.OutputFileName));
        var refAssembly = Resolve(ParameterValue(task, "OutputRefAssembly"), projectDirectory);

        return new JsonObject
        {
            ["target"] = target?.Name,
            ["targetFile"] = NormPath(target?.SourceFilePath),
            ["taskAssembly"] = NormPath(task.FromAssembly),
            ["tool"] = Arr(tool.Select(t => Norm(t).Replace('\\', '/')), sort: false),
            ["arguments"] = Arr(expanded.Select(Norm), sort: false),
            ["switches"] = Arr(expanded.Where(IsSwitch).Select(SwitchName).Distinct()),
            ["responseFiles"] = responseFiles,
            ["noConfig"] = expanded.Any(a => SwitchName(a) == "/noconfig"),
            ["analyzerConfigs"] = analyzerConfigs,
            ["analyzers"] = Arr(parsed.AnalyzerReferences.Select(a => NormPath(a.FilePath, projectDirectory))),
            ["additionalFiles"] = Arr(parsed.AdditionalFiles.Select(f => NormPath(f.Path, projectDirectory))),
            ["sources"] = Arr(parsed.SourceFiles.Select(f => NormPath(f.Path, projectDirectory))),
            ["nowarn"] = Arr(expanded.Where(a => SwitchName(a) == "/nowarn").Select(Norm), sort: false),
            ["warnaserror"] = Arr(expanded.Where(a => SwitchName(a) == "/warnaserror").Select(Norm), sort: false),
            ["ruleset"] = parsed.RuleSetPath is null ? null : NormPath(parsed.RuleSetPath, projectDirectory),
            ["errorLog"] = errorLog,
            ["roslyn"] = new JsonObject
            {
                ["generalDiagnosticOption"] = compilation.GeneralDiagnosticOption.ToString(),
                ["warningLevel"] = compilation.WarningLevel,
                ["specificDiagnosticOptions"] = specific,
                ["skipAnalyzers"] = parsed.SkipAnalyzers,
                ["errors"] = Arr(parsed.Errors.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => Norm(d.ToString()))),
            },
            ["references"] = References(task, projectDirectory),
            ["output"] = new JsonObject { ["path"] = NormPath(outputAssembly), ["sha256"] = Sha256(outputAssembly) },
            ["refOutput"] = refAssembly is null ? null : new JsonObject { ["path"] = NormPath(refAssembly), ["sha256"] = Sha256(refAssembly) },
        };
    }

    void Expand(List<string> args, string baseDirectory, List<string> output, JsonArray responseFiles, int depth)
    {
        foreach (var arg in args)
        {
            if (!arg.StartsWith('@'))
            {
                output.Add(arg);
                continue;
            }

            var full = Resolve(arg[1..].Trim('"'), baseDirectory);
            var exists = full is not null && File.Exists(full);
            responseFiles.Add(new JsonObject
            {
                ["path"] = NormPath(arg[1..].Trim('"'), baseDirectory),
                ["content"] = exists ? Norm(File.ReadAllText(full!)) : null,
            });
            output.Add("@" + NormPath(arg[1..].Trim('"'), baseDirectory));
            if (!exists || depth > 8)
            {
                continue;
            }

            var inner = new List<string>();
            foreach (var line in File.ReadAllLines(full!))
            {
                inner.AddRange(CommandLineParser.SplitCommandLineIntoArguments(line, removeHashComments: true));
            }

            Expand(inner, Path.GetDirectoryName(full!)!, output, responseFiles, depth + 1);
        }
    }

    static bool IsSwitch(string arg) => arg.Length > 1 && (arg[0] == '/' || arg[0] == '-' || arg[0] == '@');

    static string SwitchName(string arg)
    {
        if (arg.StartsWith('@'))
        {
            return "@";
        }

        if (!IsSwitch(arg))
        {
            return "";
        }

        var name = arg[1..];
        var colon = name.IndexOf(':');
        if (colon >= 0)
        {
            name = name[..colon];
        }

        name = name.TrimEnd('+', '-').ToLowerInvariant();
        return "/" + name;
    }

    static string? ParameterValue(MSTask task, string name)
    {
        var folder = task.FindChild<Folder>("Parameters");
        return folder?.Children.OfType<Property>().FirstOrDefault(p => p.Name == name)?.Value;
    }

    static IEnumerable<Item> ParameterItems(MSTask task, string name)
    {
        var folder = task.FindChild<Folder>("Parameters");
        var parameter = folder?.Children.OfType<Parameter>().FirstOrDefault(p => p.Name == name);
        if (parameter is not null)
        {
            return parameter.Children.OfType<Item>().ToList();
        }

        // MSBuild logs a one-item parameter as a plain property (no metadata).
        var single = folder?.Children.OfType<Property>().FirstOrDefault(p => p.Name == name);
        return single?.Value is { Length: > 0 } value ? [new Item { Text = value }] : [];
    }

    JsonArray References(MSTask task, string projectDirectory)
    {
        var list = new List<JsonObject>();
        foreach (var item in ParameterItems(task, "References"))
        {
            var meta = Meta(item);
            string origin;
            string? project = null, package = null;
            if (meta.GetValueOrDefault("ReferenceSourceTarget") == "ProjectReference")
            {
                origin = "projectReference";
                project = NormPath(meta.GetValueOrDefault("MSBuildSourceProjectFile", meta.GetValueOrDefault("OriginalItemSpec", "")), projectDirectory);
            }
            else if (meta.GetValueOrDefault("FrameworkReferenceName", "").Length > 0)
            {
                origin = "frameworkReference";
                package = meta.GetValueOrDefault("FrameworkReferenceName") + "/" + meta.GetValueOrDefault("FrameworkReferenceVersion", "");
            }
            else if (meta.GetValueOrDefault("NuGetPackageId", "").Length > 0)
            {
                origin = "packageReference";
                package = meta.GetValueOrDefault("NuGetPackageId") + "/" + meta.GetValueOrDefault("NuGetPackageVersion", "");
            }
            else
            {
                origin = "reference";
            }

            list.Add(new JsonObject
            {
                ["path"] = NormPath(item.Text, projectDirectory),
                ["origin"] = origin,
                ["project"] = project,
                ["package"] = package,
            });
        }

        var array = new JsonArray();
        foreach (var o in list.OrderBy(o => o["path"]!.GetValue<string>(), StringComparer.Ordinal))
        {
            array.Add(o);
        }

        return array;
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Whole-build facts
    // -----------------------------------------------------------------------------------------------------------------
    JsonArray TaskCatalog()
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var task in build.FindChildrenRecursive<MSTask>())
        {
            var target = task.GetNearestParent<Target>();
            var key = task.Name + " | " + (target?.Name ?? "") + " | " + NormPath(target?.SourceFilePath) + " | " + NormPath(task.FromAssembly);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        var array = new JsonArray();
        foreach (var (key, count) in counts)
        {
            array.Add(new JsonObject { ["task"] = key, ["count"] = count });
        }

        return array;
    }

    static bool IsAssembly(string file) =>
        file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        || file.EndsWith(".winmd", StringComparison.OrdinalIgnoreCase);

    JsonArray Copies()
    {
        var seen = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var task in build.FindChildrenRecursive<MSTask>().Where(t => t.Name == "Copy"))
        {
            var project = task.GetNearestParent<Project>();
            var baseDirectory = project?.ProjectDirectory ?? repo;
            var sources = ParameterItems(task, "SourceFiles").ToList();
            var destinations = ParameterItems(task, "DestinationFiles").ToList();
            if (destinations.Count == 0)
            {
                var folder = ParameterValue(task, "DestinationFolder")
                    ?? ParameterItems(task, "DestinationFolder").FirstOrDefault()?.Text;
                if (folder is null)
                {
                    continue;
                }

                destinations = sources.Select(s => new Item { Text = Path.Combine(folder, Path.GetFileName(s.Text)) }).ToList();
            }

            var target = task.GetNearestParent<Target>();
            for (var i = 0; i < Math.Min(sources.Count, destinations.Count); i++)
            {
                if (!IsAssembly(destinations[i].Text) && !IsAssembly(sources[i].Text))
                {
                    continue;
                }

                var sourceFull = Resolve(sources[i].Text, baseDirectory);
                var meta = Meta(sources[i]);
                var package = meta.GetValueOrDefault("NuGetPackageId", "");
                var entry = new JsonObject
                {
                    ["source"] = NormPath(sources[i].Text, baseDirectory),
                    ["destination"] = NormPath(destinations[i].Text, baseDirectory),
                    ["target"] = target?.Name,
                    ["targetFile"] = NormPath(target?.SourceFilePath),
                    ["package"] = package.Length > 0 ? package + "/" + meta.GetValueOrDefault("NuGetPackageVersion", "") : null,
                    ["sourceSha256"] = Sha256(sourceFull),
                };
                seen[entry["destination"]!.GetValue<string>() + " <= " + entry["source"]!.GetValue<string>() + " @ " + target?.Name] = entry;
            }
        }

        var array = new JsonArray();
        foreach (var entry in seen.Values)
        {
            array.Add(entry);
        }

        return array;
    }

    JsonArray ReadyToRun()
    {
        var seen = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var task in build.FindChildrenRecursive<MSTask>().Where(t => t.Name == "RunReadyToRunCompiler"))
        {
            var project = task.GetNearestParent<Project>();
            var baseDirectory = project?.ProjectDirectory ?? repo;
            foreach (var entry in ParameterItems(task, "CompilationEntry"))
            {
                var meta = Meta(entry);
                var output = Resolve(meta.GetValueOrDefault("OutputR2RImage"), baseDirectory);
                var package = meta.GetValueOrDefault("NuGetPackageId", "");
                var tool = ParameterItems(task, "Crossgen2Tool").FirstOrDefault()?.Text;
                var o = new JsonObject
                {
                    ["input"] = NormPath(entry.Text, baseDirectory),
                    ["inputSha256"] = Sha256(Resolve(entry.Text, baseDirectory)),
                    ["output"] = NormPath(output),
                    ["outputSha256"] = Sha256(output),
                    ["package"] = package.Length > 0 ? package + "/" + meta.GetValueOrDefault("NuGetPackageVersion", "") : null,
                    ["compiler"] = NormPath(tool),
                };
                seen[o["output"]!.GetValue<string>()] = o;
            }
        }

        var array = new JsonArray();
        foreach (var entry in seen.Values)
        {
            array.Add(entry);
        }

        return array;
    }

    JsonArray RuntimePacks()
    {
        var packs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var task in build.FindChildrenRecursive<MSTask>().Where(t => t.Name == "ResolveRuntimePackAssets"))
        {
            foreach (var item in ParameterItems(task, "ResolvedRuntimePacks"))
            {
                var meta = Meta(item);
                packs.Add(meta.GetValueOrDefault("NuGetPackageId", item.Text) + "/" + meta.GetValueOrDefault("NuGetPackageVersion", ""));
            }
        }

        return Arr(packs);
    }

    static JsonArray Arr(IEnumerable<string> values, bool sort = true)
    {
        var list = sort ? values.Distinct().OrderBy(v => v, StringComparer.Ordinal) : values;
        var array = new JsonArray();
        foreach (var v in list)
        {
            array.Add(v);
        }

        return array;
    }
}

static class Json
{
    public static string Write(JsonNode node)
    {
        var sorted = Sort(node);
        return sorted!.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }).Replace("\r\n", "\n") + "\n";
    }

    static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Sort(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Sort).ToArray()),
        null => null,
        _ => JsonNode.Parse(node.ToJsonString()),
    };
}
