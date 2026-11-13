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
    string repo = "";

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
        this.repo = repo;
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
                text = Regex.Replace(text, Regex.Escape(form) + @"(?:[\\/]+|(?=[""';,\])]|$|\s+(?:[-/@""]|$)))", token, RegexOptions.IgnoreCase);
            }
        }

        // WPF's temporary markup-compile project has a random name; keep the facts stable.
        return Regex.Replace(text, @"_[a-z0-9]{8}_wpftmp", "_wpftmp", RegexOptions.IgnoreCase);
    }

    // A path: absolute (relative ones are taken from the given base), tokenised, with forward slashes.
    public string NormPath(string? value, string? baseDirectory = null)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var full = value.Trim().Trim('"');
        try
        {
            full = Path.GetFullPath(Path.IsPathRooted(full) ? full : Path.Combine(baseDirectory ?? repo, full));
        }
        catch (ArgumentException)
        {
            // keep the text as given
        }

        // A file directly in a folder ABOVE the repository, up to the drive root (MSBuild probes every parent folder for
        // .editorconfig / .globalconfig / Directory.Build.*): keep how far up it is and its name. The drive root is too
        // short to be a token root, so it is handled here.
        var directory = Path.GetDirectoryName(full)?.TrimEnd('\\', '/');
        var up = 0;
        for (var d = Path.GetDirectoryName(repo.TrimEnd('\\', '/')); repo.Length > 0 && d is not null; d = Path.GetDirectoryName(d))
        {
            up++;
            if (directory is not null && string.Equals(d.TrimEnd('\\', '/'), directory, StringComparison.OrdinalIgnoreCase))
            {
                return $"<repo-parent-{up}>/" + Path.GetFileName(full);
            }
        }

        return Norm(full).Replace('\\', '/');
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
    // Any absolute-path shape, case-insensitive, at any position: a drive letter followed by a backslash or a single
    // slash, even glued right after a letter or digit (also inside \\?\C:\; a URL's scheme:// is not one); a URL-encoded
    // drive (C%3A%5C, C%3A%2F, C%3A/); and after any delimiter a UNC path (\\srv\, \\?\UNC\), //host/ (but not a URL's
    // scheme://), a POSIX root (/home, /users, /root, /opt, /tmp), an msys drive (/c/). For these last shapes a path
    // character right before them means the middle of a relative path. A drive-relative C:Users is not refused.
    const string NotInPath = @"(?<![A-Za-z0-9_.~$>)\]\\/-])";
    static readonly Regex[] Shapes =
    [
        new(@"[A-Za-z]:(?:\\|/(?!/))", RegexOptions.Compiled),
        new(@"[A-Za-z]%3[Aa](?:%5[Cc]|%2[Ff]|[\\/])", RegexOptions.Compiled),
        new(NotInPath + @"\\\\[^\\/\s""'<>|]+[\\/]", RegexOptions.Compiled),
        new(NotInPath + @"/(?:home|users|root|opt|tmp)/[^/\s""']", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(NotInPath + @"/[A-Za-z]/[^/\s""']", RegexOptions.Compiled),
    ];

    static readonly Regex SlashSlash = new(NotInPath + @"//[A-Za-z0-9._$?-]+/", RegexOptions.Compiled);
    static readonly Regex UrlScheme = new(@"(?:^|[\s""'(\[<=,;])[A-Za-z][A-Za-z0-9+.-]*:$", RegexOptions.Compiled);

    public static Match? AbsolutePath(string s)
    {
        foreach (var regex in Shapes)
        {
            var m = regex.Match(s);
            if (m.Success)
            {
                return m;
            }
        }

        for (var m = SlashSlash.Match(s); m.Success; m = m.NextMatch())
        {
            if (!UrlScheme.IsMatch(s[..m.Index]))
            {
                return m;
            }
        }

        return null;
    }

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
        if (AbsolutePath(s) is { } m)
        {
            var start = Math.Max(0, m.Index - 50);
            var around = s.Substring(start, Math.Min(s.Length - start, 110)).Replace("\r", "").Replace("\n", "\\n");
            found.Add($"{where}: ...{around}...");
            return;
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

// Global properties keep values only for reviewed property names, so `-p:Name=secret` never reaches the facts (the
// name stays visible). The MSBuild command line keeps NO property values at all, and no value of any switch that is
// not on a short reviewed list: MSBuild splits property pairs on ';' AND ',', accepts quotes and repeated switches, so
// filtering pair by pair cannot be complete. The filtered globalProperties carry the reviewed values.
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

    // Switches whose value is a fixed word or a path the tokens cover.
    static readonly HashSet<string> SafeValueSwitches = new(StringComparer.OrdinalIgnoreCase)
    {
        "m", "maxcpucount", "v", "verbosity", "tlp", "terminalloggerparameters", "tl", "terminallogger", "t", "target",
        "restore", "r", "nologo", "clp", "consoleloggerparameters", "nr", "nodereuse", "bl", "binarylogger",
    };

    // A switch anywhere in the text (an argument can hold several when the logged line's quotes are unbalanced). Its
    // value runs to the next switch or the end, so a quoted value with spaces is hidden whole; hiding a following plain
    // argument too is the safe side.
    static readonly Regex Switch = new(
        @"(?<=^|[\s""'])(?<sw>[-/]{1,2}(?<name>[A-Za-z][A-Za-z0-9_.-]*))(?<sep>[:=])(?<value>.*?)(?=\s+[""']?[-/][A-Za-z]|[""']?\s*$)",
        RegexOptions.Singleline);

    // One MSBuild command-line argument: every switch in it keeps its value only when it is on the safe list.
    public static string Argument(string arg) => Switch.Replace(arg, m =>
        SafeValueSwitches.Contains(m.Groups["name"].Value) ? m.Value : m.Groups["sw"].Value + m.Groups["sep"].Value + Hidden);
}

// Command lines whose paths may hold spaces (C:\Program Files\dotnet). Never split first and tokenise after: a split
// cuts an unquoted path at its space, and neither half matches its root any more.
static class CommandLines
{
    static readonly Regex CscName = new(@"csc(?:\.dll|\.exe)""?(?=\s|$)", RegexOptions.IgnoreCase);

    public static List<string> Split(string text) =>
        Microsoft.CodeAnalysis.CommandLineParser.SplitCommandLineIntoArguments(text, removeHashComments: false).ToList();

    // Csc's logged line is "<compiler> <arguments>"; the compiler path is written unquoted even when it holds spaces.
    // The compiler ends at its own name (csc.dll / csc.exe); the arguments after it are split quote-aware (MSBuild
    // quotes every argument value that holds a space).
    public static (List<string> Tool, List<string> Arguments) SplitCsc(string raw)
    {
        var m = CscName.Match(raw);
        if (!m.Success)
        {
            return ([], Split(raw));
        }

        var head = raw[..(m.Index + m.Length)].Trim();
        var rest = Split(raw[(m.Index + m.Length)..]);
        List<string> tool;
        if (head.Contains('"'))
        {
            tool = Split(head);
        }
        else
        {
            var parts = Regex.Split(head, @"\s+exec\s+", RegexOptions.IgnoreCase);
            tool = parts.Length == 2 ? [parts[0], "exec", parts[1]] : [head];
        }

        return (tool, rest);
    }

    // The MSBuild command line: tokenise the whole raw text first (an unquoted SDK path with spaces stays whole), then
    // split quote-aware, filter property values (PropertyFilter), and use forward slashes.
    public static string MsBuild(string raw, PathTokens tokens)
    {
        var text = tokens.Norm(raw.Trim());
        if (text.Length > 1 && text[0] == '"' && text[^1] == '"' && text.Count(c => c == '"') == 2)
        {
            text = text[1..^1];
        }

        return string.Join(" ", Split(text).Select(arg => tokens.Norm(PropertyFilter.Argument(arg)).Replace('\\', '/')));
    }
}
