using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class Program
{
    private const string Rule = "BOO-LINT-002A";

    public static int Main(string[] args)
    {
        if (args is ["--self-test"])
            return SelfTest();
        if (args is not ["--root", var root])
        {
            Console.Error.WriteLine($"{Rule} usage: --root <git checkout> | --self-test");
            return 2;
        }
        try
        {
            return Check(root);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or DecoderFallbackException)
        {
            Console.Error.WriteLine($"{Rule} cannot read the committed source snapshot ({error.GetType().Name}); failing closed. Fix: use a full git checkout with git and the pinned SDK available.");
            return 1;
        }
    }

    private static byte[] Git(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("git unavailable");
        var diagnostics = process.StandardError.ReadToEndAsync();
        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        process.WaitForExit();
        _ = diagnostics.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("git snapshot read failed");
        return output.ToArray();
    }

    private static int[] DisableLines(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        return tree.GetRoot().DescendantTrivia(descendIntoTrivia: true)
            .Select(trivia => trivia.GetStructure())
            .OfType<PragmaWarningDirectiveTriviaSyntax>()
            .Where(directive => directive.DisableOrRestoreKeyword.IsKind(SyntaxKind.DisableKeyword))
            .Select(directive => directive.GetLocation().GetLineSpan().StartLinePosition.Line + 1)
            .ToArray();
    }

    private static int Check(string root)
    {
        var entries = new UTF8Encoding(false, true).GetString(Git(root, "ls-tree", "-r", "-z", "--full-tree", "HEAD", "--", "src"));
        var files = 0;
        var violations = 0;
        foreach (var entry in entries.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = entry.IndexOf('\t');
            if (separator < 0)
                throw new InvalidOperationException("invalid git tree record");
            var path = entry[(separator + 1)..];
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;
            var metadata = entry[..separator].Split(' ');
            if (metadata.Length != 3 || metadata[1] != "blob" || metadata[0] is not ("100644" or "100755"))
                throw new InvalidOperationException("C# source must be a regular git blob");
            using var bytes = new MemoryStream(Git(root, "cat-file", "blob", metadata[2]));
            using var reader = new StreamReader(bytes, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            foreach (var line in DisableLines(reader.ReadToEnd()))
            {
                Console.Error.WriteLine($"{Rule} {path}:{line}: warning-disable directive. Fix: remove the directive and fix the diagnostic; do not silence it with project/config switches. See docs/quality/no-warning-directives.md.");
                violations++;
            }
            files++;
        }
        if (files == 0)
            throw new InvalidOperationException("no committed src C# files found");
        Console.WriteLine($"NO_WARNING_DIRECTIVES_{(violations == 0 ? "OK" : "FAILED")} files={files} violations={violations}");
        return violations == 0 ? 0 : 1;
    }

    private static int SelfTest()
    {
        (string Name, string Source, int[] Lines)[] cases =
        [
            ("empty", "", []),
            ("numeric warning", "#pragma warning disable 1591\n", [1]),
            ("named warning", "#pragma warning disable CA1031\n", [1]),
            ("all warnings", "#pragma warning disable\n", [1]),
            ("multiple warning ids", "#pragma warning disable CS1591, CA1031\n", [1]),
            ("restore", "#pragma warning restore CA1031\n", []),
            ("line comment", "// #pragma warning disable CA1031\n", []),
            ("block comment", "/*\n#pragma warning disable CA1031\n*/", []),
            ("string", "const string s = \"#pragma warning disable CA1031\";", []),
            ("verbatim string", "const string s = @\"\n#pragma warning disable CA1031\n\";", []),
            ("raw string", "const string s = \"\"\"\n#pragma warning disable CA1031\n\"\"\";", []),
            ("inactive branch", "#if false\n#pragma warning disable CA1031\n#endif", [2]),
            ("space after hash", "# pragma warning disable CA1031\n", [1]),
            ("two directives", "#pragma warning disable CA1031\n#pragma warning disable CS1591", [1, 2]),
            ("physical line despite line mapping", "#line 9000\n#pragma warning disable CA1031", [2]),
            ("nullable outside this rule", "#nullable disable\n", []),
            ("typed baseline outside this rule", "[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage(\"Usage\", \"CA1031\")]", []),
            ("format pragma conservatively rejected", "#pragma warning disable format\n", [1]),
            ("CR line endings", "// comment\r#pragma warning disable CA1031\r", [2]),
            ("Unicode newline", "// comment\u2028#pragma warning disable CA1031\n", [2])
        ];
        foreach (var sample in cases)
        {
            if (!DisableLines(sample.Source).SequenceEqual(sample.Lines))
            {
                Console.Error.WriteLine($"{Rule} SELF_TEST_FAILED {sample.Name}");
                return 1;
            }
        }
        Console.WriteLine($"NO_WARNING_DIRECTIVES_SELF_TEST_OK {cases.Length} Roslyn syntax cases");
        return 0;
    }
}
