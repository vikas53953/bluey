namespace Boo.Core.Tests;

/// <summary>
/// Windows silently rewrites some names when it opens them: it drops trailing dots and spaces from
/// each segment ("notes." opens the sibling "notes"), maps DOS device names ("con", "nul.txt") to
/// devices, and treats wildcards specially. A scoped path must open exactly the name the approval
/// card shows, so every name Windows would rewrite is refused, and anything that resolves names
/// exactly what was requested. (STATUS "Known residuals": "notes." landed on "notes".)
/// </summary>
public sealed class ScopedFilesNameFidelityTests
{
    [Fact]
    public void A_trailing_dot_never_lands_on_the_sibling_file()
    {
        WithRoot(root =>
        {
            File.WriteAllText(Path.Combine(root, "notes"), "sibling");
            Assert.Throws<ArgumentException>(() => ScopedFiles.Resolve(root, "notes."));
        });
    }

    [Theory]
    // Trailing dots and spaces, in the last segment or a folder segment.
    [InlineData("notes.")]
    [InlineData("notes ")]
    [InlineData("notes. .")]
    [InlineData("notes.txt.")]
    [InlineData("sub\\notes.")]
    [InlineData("sub.\\notes.txt")]
    [InlineData("sub /notes.txt")]
    // DOS device names, with or without an extension, in any folder.
    [InlineData("con")]
    [InlineData("NUL")]
    [InlineData("nul.txt")]
    [InlineData("sub\\aux")]
    [InlineData("COM1")]
    [InlineData("lpt9.log")]
    [InlineData("com¹")]
    [InlineData("CONIN$")]
    [InlineData("prn .txt")]
    // Wildcards and other characters Windows does not allow in a file name.
    [InlineData("*.txt")]
    [InlineData("note?.txt")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    [InlineData("a\"b")]
    [InlineData("tab\there")]
    public void Names_windows_would_rewrite_are_rejected(string relative)
    {
        WithRoot(root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            string? resolved = null;
            var exception = Record.Exception(() => resolved = ScopedFiles.Resolve(root, relative));
            Assert.True(exception is ArgumentException,
                $"'{relative}' must be refused with ArgumentException, but " + (exception is null ? $"it resolved to '{resolved}'" : $"it threw {exception.GetType().Name}: {exception.Message}"));
        });
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData(".gitignore")]
    [InlineData("sub\\file.name.txt")]
    [InlineData("sub/a b.txt")]
    [InlineData("console.txt")]
    [InlineData("nulls.txt")]
    [InlineData("com10.txt")]
    [InlineData("con-notes.txt")]
    [InlineData("auxiliary")]
    public void Ordinary_names_resolve_to_exactly_the_requested_name(string relative)
    {
        WithRoot(root =>
        {
            var expected = Path.Combine(new[] { Path.GetFullPath(root) }.Concat(relative.Split('\\', '/')).ToArray());
            Assert.Equal(expected, ScopedFiles.Resolve(root, relative));
        });
    }

    [Fact]
    public void Every_resolved_path_names_exactly_what_was_requested()
    {
        string[] stems = ["notes", "a b", "con", "x.txt", ".hidden", "sub", "...", "com1", "lpt²", "file"];
        string[] tails = ["", ".", " ", ". ", " .", "..", ".txt", ".txt.", ".txt ", "::$DATA", "*", "?", "\t"];
        string[] folders = ["", "sub\\", "sub./", "sub /"];
        WithRoot(root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var resolvedCount = 0;
            foreach (var folder in folders)
            foreach (var stem in stems)
            foreach (var tail in tails)
            {
                var relative = folder + stem + tail;
                string resolved;
                try
                {
                    resolved = ScopedFiles.Resolve(root, relative);
                }
                catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                var requested = string.Join(Path.DirectorySeparatorChar, relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries));
                Assert.Equal(prefix + requested, resolved);
                resolvedCount++;
            }

            // The sweep must exercise both sides: some names resolve, some are refused.
            Assert.InRange(resolvedCount, 1, folders.Length * stems.Length * tails.Length - 1);
        });
    }

    private static void WithRoot(Action<string> test)
    {
        var root = Directory.CreateTempSubdirectory("boo-scoped-fidelity").FullName;
        try
        {
            test(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
