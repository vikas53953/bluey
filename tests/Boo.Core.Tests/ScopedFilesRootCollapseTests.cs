namespace Boo.Core.Tests;

/// <summary>
/// Names made only of dots are not "." or ".." but Windows normalisation
/// still collapses them, which turned "..." into the root directory itself
/// (found by UntrustedInputFuzzTests seed 7919 on windows-latest).
/// </summary>
public sealed class ScopedFilesRootCollapseTests
{
    [Theory]
    [InlineData("...")]
    [InlineData("....")]
    [InlineData("sub\\...")]
    [InlineData("sub/.../...")]
    public void A_relative_path_never_resolves_to_the_root_itself(string relative)
    {
        var root = Directory.CreateTempSubdirectory("boo-scoped-root").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            var rootPrefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string resolved;
            try
            {
                resolved = ScopedFiles.Resolve(root, relative);
            }
            catch (ArgumentException)
            {
                // Windows: the dots collapse and the guard refuses the root.
                return;
            }

            // Linux: dot-only names are ordinary file names and stay inside the root.
            Assert.StartsWith(rootPrefix, resolved, StringComparison.OrdinalIgnoreCase);
            Assert.True(resolved.TrimEnd(Path.DirectorySeparatorChar).Length > rootPrefix.Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void An_ordinary_name_still_resolves_inside_the_root()
    {
        var root = Directory.CreateTempSubdirectory("boo-scoped-root").FullName;
        try
        {
            var resolved = ScopedFiles.Resolve(root, "notes/today.txt");
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "notes", "today.txt")), resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
