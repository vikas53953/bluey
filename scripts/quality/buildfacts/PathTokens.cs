using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// Machine roots -> stable tokens, and the guard that refuses any machine path left after that.
//
// Every root comes from the build being read (its evaluated properties: the SDK, NuGet folders, Program Files, the
// Windows SDK, temp, home, the runner's tool cache) or from this process, plus every folder above the repository.
// Each root is matched case-insensitively, with either slash, and in its 8.3 short and long forms, longest first, so
// `C:\Program Files (x86)\...` never becomes `<programfiles>/ (x86)\...`. Values are read only to find roots; no
// property value is ever written by this class.
sealed class PathTokens
{
    // property name -> token. A value may be a ';' list. "dir:" means "the folder of this file".
    public static readonly (string Property, string Token)[] FromProperties =
    [
        ("NetCoreRoot", "<dotnet>/"), ("DOTNET_ROOT", "<dotnet>/"), ("DOTNET_INSTALL_DIR", "<dotnet>/"),
        ("dir:DOTNET_HOST_PATH", "<dotnet>/"),
        ("NuGetPackageRoot", "<nuget>/"), ("NuGetPackageFolders", "<nuget>/"), ("RestorePackagesPath", "<nuget>/"),
        ("NUGET_PACKAGES", "<nuget>/"), ("NuGetFallbackFolder", "<nuget-fallback>/"),
        ("TargetPlatformSdkPath", "<winsdk>/"), ("WindowsSdkPath", "<winsdk>/"), ("WindowsSdkDir", "<winsdk>/"),
        ("NETFXKitsDir", "<winsdk-netfx>/"), ("SDKReferenceDirectoryRoot", "<sdk-references>/"),
        ("SDKExtensionDirectoryRoot", "<sdk-extensions>/"), ("TargetFrameworkRootPath", "<reference-assemblies>/"),
        ("FrameworkPathOverride", "<framework-override>/"),
        ("ProgramFiles(x86)", "<programfiles-x86>/"), ("MSBuildProgramFiles32", "<programfiles-x86>/"),
        ("ProgramFiles", "<programfiles>/"), ("ProgramW6432", "<programfiles>/"),
        ("ProgramData", "<programdata>/"), ("ALLUSERSPROFILE", "<programdata>/"),
        ("windir", "<windows>/"), ("SystemRoot", "<windows>/"),
        ("TEMP", "<temp>/"), ("TMP", "<temp>/"), ("RUNNER_TEMP", "<temp>/"),
        ("RUNNER_TOOL_CACHE", "<toolcache>/"), ("AGENT_TOOLSDIRECTORY", "<toolcache>/"),
        ("LOCALAPPDATA", "<localappdata>/"), ("APPDATA", "<appdata>/"),
        ("USERPROFILE", "<home>/"), ("HOME", "<home>/"),
    ];

    readonly List<(string Root, string Token)> roots = [];
    bool sorted;

    public void Add(string? root, string token)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        foreach (var form in Forms(root.Trim().Trim('"')))
        {
            var trimmed = form.TrimEnd('\\', '/');
            if (trimmed.Length < 4 || !Rooted(trimmed)
                || roots.Any(r => string.Equals(r.Root, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            roots.Add((trimmed, token));
            sorted = false;
        }
    }

    // Roots from one evaluation's properties (the build's own view of this machine).
    public void AddFromProperties(IReadOnlyDictionary<string, string> props)
    {
        foreach (var (property, token) in FromProperties)
        {
            var name = property.StartsWith("dir:", StringComparison.Ordinal) ? property[4..] : property;
            if (!props.TryGetValue(name, out var value))
            {
                continue;
            }

            foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Add(property.StartsWith("dir:", StringComparison.Ordinal) ? SafeDirectory(part) : part, token);
            }
        }
    }

    public void AddRepo(string repo)
    {
        Add(repo, "");
        var up = 0;
        for (var d = Path.GetDirectoryName(repo.TrimEnd('\\', '/')); d is not null; d = Path.GetDirectoryName(d))
        {
            up++;
            Add(d, $"<repo-parent-{up}>/");
        }
    }

    public string Norm(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }

        if (!sorted)
        {
            roots.Sort((a, b) => b.Root.Length.CompareTo(a.Root.Length));
            sorted = true;
        }

        foreach (var (root, token) in roots)
        {
            var slash = root.Replace('\\', '/');
            foreach (var form in new[] { root, slash, slash.Replace("/", "\\\\") })
            {
                if (text.IndexOf(form, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                // A root counts only as a whole folder: followed by a separator or the end of the path.
                text = Regex.Replace(text, Regex.Escape(form) + @"(?:[\\/]+|(?=[""';,\s\])]|$))", token, RegexOptions.IgnoreCase);
            }
        }

        // WPF's temporary markup-compile project has a random name; keep the facts stable.
        return Regex.Replace(text, @"_[a-z0-9]{8}_wpftmp", "_wpftmp", RegexOptions.IgnoreCase);
    }

    static bool Rooted(string path) =>
        Regex.IsMatch(path, @"^[A-Za-z]:[\\/]") || path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith('/');

    static string? SafeDirectory(string file)
    {
        try
        {
            return Path.GetDirectoryName(file);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // The path as given, its full form, and on Windows its 8.3 short and long forms (when the folder exists).
    static IEnumerable<string> Forms(string root)
    {
        yield return root;
        string? full = null;
        try
        {
            full = Path.GetFullPath(root);
        }
        catch (ArgumentException)
        {
        }

        if (full is not null && full != root)
        {
            yield return full;
        }

        if (!OperatingSystem.IsWindows() || full is null || !Directory.Exists(full))
        {
            yield break;
        }

        foreach (var other in new[] { Native.Short(full), Native.Long(full) })
        {
            if (other is not null && !string.Equals(other, full, StringComparison.OrdinalIgnoreCase))
            {
                yield return other;
            }
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // The guard: every string value in the facts, by its JSON path.
    // -----------------------------------------------------------------------------------------------------------------
    static readonly Regex Drive = new(@"(?<![A-Za-z0-9])[A-Za-z]:[\\/]", RegexOptions.Compiled);
    static readonly Regex Unc = new(@"(?:^|[\s""'=(])\\\\[A-Za-z0-9.$-]+\\", RegexOptions.Compiled);
    static readonly Regex Home = new(@"(?:^|[\s""'=])/(?:home|Users)/[^/\s""]+", RegexOptions.Compiled);

    public static List<string> Leaks(JsonNode? node, string? userName)
    {
        var found = new List<string>();
        Walk(node, "$", found, userName);
        return found;
    }

    static void Walk(JsonNode? node, string where, List<string> found, string? userName)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o)
                {
                    Check(key, where + ".<key " + key + ">", found, userName);
                    Walk(value, where + "." + key, found, userName);
                }

                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    Walk(a[i], $"{where}[{i}]", found, userName);
                }

                break;
            case JsonValue v when v.TryGetValue<string>(out var s):
                Check(s, where, found, userName);
                break;
        }
    }

    static void Check(string s, string where, List<string> found, string? userName)
    {
        foreach (var regex in new[] { Drive, Unc, Home })
        {
            var m = regex.Match(s);
            if (m.Success)
            {
                var start = Math.Max(0, m.Index - 50);
                var around = s.Substring(start, Math.Min(s.Length - start, 110)).Replace("\r", "").Replace("\n", "\\n");
                found.Add($"{where}: ...{around}...");
                return;
            }
        }

        if (userName is { Length: >= 3 } && s.Contains(userName, StringComparison.OrdinalIgnoreCase))
        {
            found.Add($"{where}: holds the user name ({userName.Length} letters)");
        }
    }

    static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetShortPathNameW")]
        static extern uint GetShortPathName(string longPath, char[] buffer, uint size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetLongPathNameW")]
        static extern uint GetLongPathName(string shortPath, char[] buffer, uint size);

        public static string? Short(string path) => Call(GetShortPathName, path);

        public static string? Long(string path) => Call(GetLongPathName, path);

        static string? Call(Func<string, char[], uint, uint> api, string path)
        {
            var buffer = new char[1024];
            var n = api(path, buffer, (uint)buffer.Length);
            return n == 0 || n >= buffer.Length ? null : new string(buffer, 0, (int)n);
        }
    }
}

// Global properties and the MSBuild command line: only reviewed property names keep their values, so a future
// `-p:Name=secret` never reaches the facts (the name is kept so the property stays visible).
static class PropertyFilter
{
    public static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "Configuration", "Platform", "TargetFramework", "TargetFrameworks", "RuntimeIdentifier", "SelfContained",
        "_IsPublishing", "PublishDir", "PublishReadyToRun", "SolutionName", "SolutionFileName", "SolutionExt",
        "SolutionDir", "SolutionPath", "BuildingSolutionFile", "_CommandLineDefinedOutputPath",
        "_CommandLineDefinedRuntimeIdentifier", "_CommandLineDefinedSelfContained",
        "DOTNET_CLI_DISABLE_PUBLISH_AND_PACK_RELEASE", "NuGetInteractive", "MSBuildIsRestoring",
        "ExcludeRestorePackageImports", "EnableDefaultCompileItems", "EnableDefaultEmbeddedResourceItems",
        "EnableDefaultNoneItems", "BuildProjectReferences", "IsGraphBuild", "Optimize", "DebugType",
        "ContinuousIntegrationBuild", "Deterministic", "OutDir", "OutputPath", "IntermediateOutputPath",
        "BaseIntermediateOutputPath", "RestoreUseStaticGraphEvaluation", "AssemblyName", "OutputType",
    };

    public const string Hidden = "<value not recorded>";

    public static string Pair(string key, string value) => key + "=" + (Allowed.Contains(key) ? value : Hidden);

    static readonly Regex PropertySwitch =
        new(@"^(?<sw>[-/]{1,2}(?:p|property|rp|restoreproperty)):(?<rest>.*)$", RegexOptions.IgnoreCase);

    // One MSBuild command-line argument with every property value not on the list replaced.
    public static string Argument(string arg)
    {
        var m = PropertySwitch.Match(arg);
        if (!m.Success)
        {
            return arg;
        }

        var pairs = m.Groups["rest"].Value.Split(';').Select(pair =>
        {
            var eq = pair.IndexOf('=');
            return eq < 0 ? pair : Pair(pair[..eq], pair[(eq + 1)..]);
        });
        return m.Groups["sw"].Value + ":" + string.Join(";", pairs);
    }
}
