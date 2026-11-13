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

        Console.WriteLine($"BUILDFACTS SELF-TEST: OK ({cases.Length} runner-layout paths tokenised, leak guard names the "
            + "JSON path, property values filtered)");
        return 0;
    }
}
