using System.Text.Json.Nodes;

// BuildFacts --self-test: the path tokens on a GitHub-runner-like layout (SDK under Program Files, NuGet under the
// runner's profile, temp on D:, the 8.3 short forms), the leak guard naming the JSON path of the offending value, and
// the property filter on global properties and the MSBuild command line. Exit 1 on any failure.
static class SelfTest
{
    public static int Run()
    {
        var failures = new List<string>();
        var t = new PathTokens();
        t.AddRepo(@"D:\a\bluey\bluey");
        t.AddFromProperties(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NetCoreRoot"] = @"C:\Program Files\dotnet\",
            ["DOTNET_HOST_PATH"] = @"C:\Program Files\dotnet\dotnet.exe",
            ["NuGetPackageRoot"] = @"C:\Users\runneradmin\.nuget\packages\",
            ["NuGetPackageFolders"] = @"C:\Users\runneradmin\.nuget\packages\;C:\Program Files\dotnet\sdk\NuGetFallbackFolder",
            ["ProgramFiles"] = @"C:\Program Files",
            ["ProgramFiles(x86)"] = @"C:\Program Files (x86)",
            ["TargetPlatformSdkPath"] = @"C:\Program Files (x86)\Windows Kits\10\",
            ["RUNNER_TEMP"] = @"D:\a\_temp",
            ["TEMP"] = @"C:\Users\RUNNER~1\AppData\Local\Temp",
            ["USERPROFILE"] = @"C:\Users\runneradmin",
            ["RUNNER_TOOL_CACHE"] = @"C:\hostedtoolcache\windows",
            ["windir"] = @"C:\Windows",
        });
        t.Add(@"C:\PROGRA~1\dotnet", "<dotnet>/");   // what GetShortPathName returns on the runner

        var cases = new (string In, string Out)[]
        {
            (@"C:\Program Files\dotnet\sdk\10.0.403\Roslyn\bincore\csc.exe", @"<dotnet>/sdk\10.0.403\Roslyn\bincore\csc.exe"),
            ("c:/program files/dotnet/packs/Microsoft.NETCore.App.Ref/10.0.14/ref", "<dotnet>/packs/Microsoft.NETCore.App.Ref/10.0.14/ref"),
            (@"C:\PROGRA~1\dotnet\dotnet.exe", "<dotnet>/dotnet.exe"),
            (@"C:\Program Files (x86)\Windows Kits\10\UnionMetadata\10.0.19041.0\Windows.winmd", @"<winsdk>/UnionMetadata\10.0.19041.0\Windows.winmd"),
            (@"C:\Program Files (x86)\Reference Assemblies\Microsoft", @"<programfiles-x86>/Reference Assemblies\Microsoft"),
            (@"C:\Program Files\Git\bin", @"<programfiles>/Git\bin"),
            (@"C:\Users\runneradmin\.nuget\packages\naudio\2.2.1\lib\NAudio.dll", @"<nuget>/naudio\2.2.1\lib\NAudio.dll"),
            (@"C:\Users\RUNNER~1\AppData\Local\Temp\tmp1.rsp", "<temp>/tmp1.rsp"),
            (@"D:\a\_temp\buildfacts-fixtures\x", @"<temp>/buildfacts-fixtures\x"),
            (@"D:\a\bluey\bluey\src\Boo.App\obj\Boo.dll", @"src\Boo.App\obj\Boo.dll"),
            (@"D:\a\bluey\.editorconfig", "<repo-parent-1>/.editorconfig"),
            (@"C:\hostedtoolcache\windows\Python\3.12", @"<toolcache>/Python\3.12"),
            (@"C:\Users\runneradmin\AppData\Roaming\x", @"<home>/AppData\Roaming\x"),
            ("\"C:\\Program Files\\dotnet\\dotnet.exe\" exec csc.dll", "\"<dotnet>/dotnet.exe\" exec csc.dll"),
            ("Exists('C:\\Program Files\\dotnet\\sdk\\10.0.403\\\\vCurrent\\x.targets')", "Exists('<dotnet>/sdk\\10.0.403\\\\vCurrent\\x.targets')"),
        };
        foreach (var (input, expected) in cases)
        {
            var got = t.Norm(input);
            if (got != expected)
            {
                failures.Add($"tokens: {input} -> {got}, expected {expected}");
            }
        }

        // The guard names the JSON path of what is left.
        var facts = JsonNode.Parse("""{"binlogs":[{"units":[{"imports":["<dotnet>/a.props","E:\\other\\b.props"]}]}]}""");
        var leaks = PathTokens.Leaks(facts, "runneradmin");
        if (leaks.Count != 1 || !leaks[0].StartsWith("$.binlogs[0].units[0].imports[1]: ", StringComparison.Ordinal))
        {
            failures.Add($"leak guard: expected one leak at $.binlogs[0].units[0].imports[1], got [{string.Join(" | ", leaks)}]");
        }

        foreach (var bad in new[] { @"\\server\share\x", "/home/runner/work/x", "C:/Program Files/dotnet", "runneradmin" })
        {
            if (PathTokens.Leaks(JsonValue.Create(bad), "runneradmin").Count != 1)
            {
                failures.Add($"leak guard missed: {bad}");
            }
        }

        if (PathTokens.Leaks(JsonValue.Create(@"<dotnet>/sdk\10.0.403\\Microsoft\x"), "runneradmin").Count != 0)
        {
            failures.Add("leak guard: a tokenised path with doubled backslashes must pass");
        }

        Spaces(failures);

        // Property values: only reviewed names keep them.
        var pair = PropertyFilter.Pair("ApiToken", "abc123secret");
        if (pair != "ApiToken=" + PropertyFilter.Hidden || PropertyFilter.Pair("Configuration", "Release") != "Configuration=Release")
        {
            failures.Add($"global properties: {pair}");
        }

        var argument = PropertyFilter.Argument("--property:Configuration=Release;Secret=xyz789");
        var other = PropertyFilter.Argument("-p:Other=hush42");
        if (argument.Contains("xyz789", StringComparison.Ordinal) || other.Contains("hush42", StringComparison.Ordinal)
            || !argument.Contains("Configuration=Release", StringComparison.Ordinal))
        {
            failures.Add($"command line: {argument} / {other}");
        }

        if (failures.Count > 0)
        {
            Console.WriteLine("BUILDFACTS SELF-TEST: FAIL");
            failures.ForEach(f => Console.WriteLine("  - " + f));
            return 1;
        }

        Console.WriteLine($"BUILDFACTS SELF-TEST: OK ({cases.Length} runner-layout paths tokenised, every field kind on roots with spaces, leak guard names the "
            + "JSON path, property values filtered)");
        return 0;
    }

    // Every kind of field the reader writes, on roots that hold spaces (the windows-latest SDK is under
    // C:\Program Files\dotnet): the Csc compiler path (unquoted, quoted, dotnet exec), Csc arguments, the MSBuild command
    // line (one outer quote pair, unquoted exe), single paths (imports, copies, references, outputs), relative paths,
    // folders above the repo, and file contents (analyzer configs, response files, skipped-import reasons). Then the
    // guard must find nothing left.
    static void Spaces(List<string> failures)
    {
        var t = new PathTokens();
        t.AddRepo(@"D:\a\my repo\bluey");
        t.AddFromProperties(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NetCoreRoot"] = @"C:\Program Files\dotnet\",
            ["NuGetPackageRoot"] = @"C:\Users\runner admin\.nuget\packages\",
            ["ProgramFiles(x86)"] = @"C:\Program Files (x86)",
            ["TargetPlatformSdkPath"] = @"C:\Program Files (x86)\Windows Kits\10\",
            ["RUNNER_TEMP"] = @"D:\a\_temp",
        });
        t.Add(@"C:\PROGRA~1\dotnet", "<dotnet>/");
        var out_ = new JsonObject();
        void Expect(string field, string got, string expected)
        {
            out_[field] = got;
            if (got != expected)
            {
                failures.Add($"spaces, {field}: got {got}, expected {expected}");
            }
        }

        const string Csc = @"C:\Program Files\dotnet\sdk\10.0.403\Roslyn\bincore\csc.exe";
        var rawLines = new[]
        {
            Csc + @" /noconfig /reference:""C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\10.0.14\ref\net10.0\System.dll"" /analyzerconfig:""D:\a\my repo\bluey\src\Boo.App\obj\x.editorconfig"" /out:obj\Release\Boo.dll ""D:\a\my repo\bluey\src\Boo.App\App.cs"" @""D:\a\_temp\extra.rsp""",
            "\"" + Csc + "\" /noconfig /out:obj\\Release\\Boo.dll",
            @"C:\Program Files\dotnet\dotnet.exe exec C:\Program Files\dotnet\sdk\10.0.403\Roslyn\bincore\csc.dll /noconfig",
            @"""C:\Program Files\dotnet\dotnet.exe"" exec ""C:\Program Files\dotnet\sdk\10.0.403\Roslyn\bincore\csc.dll"" /noconfig",
        };
        var expectedTools = new[]
        {
            "<dotnet>/sdk/10.0.403/Roslyn/bincore/csc.exe",
            "<dotnet>/sdk/10.0.403/Roslyn/bincore/csc.exe",
            "<dotnet>/dotnet.exe exec <dotnet>/sdk/10.0.403/Roslyn/bincore/csc.dll",
            "<dotnet>/dotnet.exe exec <dotnet>/sdk/10.0.403/Roslyn/bincore/csc.dll",
        };
        for (var i = 0; i < rawLines.Length; i++)
        {
            var (tool, rest) = CommandLines.SplitCsc(rawLines[i]);
            Expect($"csc[{i}].tool", string.Join(" ", tool.Select(x => x == "exec" ? x : t.NormPath(x))), expectedTools[i]);
            if (rest.Count == 0 || rest[0] != "/noconfig")
            {
                failures.Add($"spaces, csc[{i}]: arguments must start right after the compiler, got [{string.Join(" | ", rest)}]");
            }

            out_[$"csc[{i}].arguments"] = string.Join(" | ", rest.Select(a => t.Norm(a)));
        }

        var args = CommandLines.SplitCsc(rawLines[0]).Arguments.Select(a => t.Norm(a)).ToList();
        // Roslyn keeps the quotes inside a switch value (it reads them itself); the root is tokenised either way.
        Expect("csc.arguments[1]", args[1], "/reference:\"<dotnet>/packs\\Microsoft.NETCore.App.Ref\\10.0.14\\ref\\net10.0\\System.dll\"");
        Expect("csc.arguments[2]", args[2], "/analyzerconfig:\"src\\Boo.App\\obj\\x.editorconfig\"");
        Expect("csc.arguments[4]", args[4], "src\\Boo.App\\App.cs");
        Expect("csc.arguments[5]", args[5], "@\"<temp>/extra.rsp\"");

        Expect("commandLine", CommandLines.MsBuild(
            @"""C:\Program Files\dotnet\sdk\10.0.403\MSBuild.exe -maxcpucount --property:Configuration=Release --property:Token=s3cr3t D:\a\my repo\bluey\Boo.sln -bl:artifacts/binlog/build.binlog""", t),
            "<dotnet>/sdk/10.0.403/MSBuild.exe -maxcpucount --property:Configuration=Release --property:Token="
            + PropertyFilter.Hidden + " Boo.sln -bl:artifacts/binlog/build.binlog");
        Expect("commandLine (quoted exe)", CommandLines.MsBuild(
            @"""C:\Program Files\dotnet\sdk\10.0.403\MSBuild.exe"" -restore ""D:\a\my repo\bluey\Boo.sln""", t),
            "<dotnet>/sdk/10.0.403/MSBuild.exe -restore Boo.sln");

        Expect("imports", t.NormPath(@"C:\Program Files\dotnet\sdk\10.0.403\Sdks\Microsoft.NET.Sdk\Sdk\Sdk.props"),
            "<dotnet>/sdk/10.0.403/Sdks/Microsoft.NET.Sdk/Sdk/Sdk.props");
        Expect("imports (slash form, trailing slash)", t.NormPath("c:/program files/dotnet/sdk/"), "<dotnet>/sdk/");
        Expect("imports (8.3)", t.NormPath(@"C:\PROGRA~1\dotnet\sdk\10.0.403\Microsoft.Common.props"),
            "<dotnet>/sdk/10.0.403/Microsoft.Common.props");
        Expect("references (Windows SDK)", t.NormPath(@"C:\Program Files (x86)\Windows Kits\10\UnionMetadata\10.0.19041.0\Windows.winmd"),
            "<winsdk>/UnionMetadata/10.0.19041.0/Windows.winmd");
        Expect("references (NuGet with a space)", t.NormPath(@"C:\Users\runner admin\.nuget\packages\naudio\2.2.1\lib\NAudio.dll"),
            "<nuget>/naudio/2.2.1/lib/NAudio.dll");
        Expect("copies (repo with a space)", t.NormPath(@"D:\a\my repo\bluey\src\Boo.App\obj\Release\Boo.dll"),
            "src/Boo.App/obj/Release/Boo.dll");
        Expect("copies (relative to a project)", t.NormPath(@"obj\Release\Boo.dll", @"D:\a\my repo\bluey\src\Boo.App"),
            "src/Boo.App/obj/Release/Boo.dll");
        Expect("editorConfigItems (above the repo)", t.NormPath(@"D:\a\my repo\.editorconfig"), "<repo-parent-1>/.editorconfig");
        Expect("analyzerConfigs.content",
            t.Norm("build_property.ProjectDir = D:\\a\\my repo\\bluey\\src\\Boo.App\\\r\n[D:/a/my repo/bluey/src/**.cs]\r\n"),
            "build_property.ProjectDir = src\\Boo.App\\\r\n[src/**.cs]\r\n");
        Expect("responseFiles.content", t.Norm(@"/reference:""C:\Program Files\dotnet\packs\a.dll"""),
            @"/reference:""<dotnet>/packs\a.dll""");
        Expect("importsSkipped", t.Norm(@"Exists('C:\Program Files (x86)\Windows Kits\10\\Include\x.props')"),
            @"Exists('<winsdk>/Include\x.props')");

        var leaks = PathTokens.Leaks(out_, "runneradmin");
        if (leaks.Count != 0)
        {
            failures.Add("spaces: machine paths left: " + string.Join(" | ", leaks));
        }
    }
}
