using Boo.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Boo.Core.Tests;

public sealed class FileWriteBoundaryTests
{
    private const long Generation = 107;
    private const int AgreedMaximumWriteBytes = 4_194_304;

    [Fact]
    public void Exactly_the_agreed_four_mib_utf8_payload_is_permitted()
    {
        WithOwnedRoot(root =>
        {
            // 2,097,151 two-byte characters plus two ASCII bytes is exactly
            // the independently agreed 4 MiB UTF-8 boundary.
            var contents = new string('é', 2_097_151) + "ab";
            Assert.Equal(AgreedMaximumWriteBytes, Encoding.UTF8.GetByteCount(contents));

            var request = new ToolRequest(
                "write_file",
                "exact-boundary.txt",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["contents"] = contents
                },
                Generation);

            var plan = FileActionResolver.Resolve(request, Authority(root));

            Assert.Equal(Path.GetFullPath(Path.Combine(root, "exact-boundary.txt")), plan.CanonicalPath);
            Assert.Equal(contents, plan.Contents);
            Assert.False(plan.Overwrite);
        });
    }

    [Fact]
    public void One_utf8_byte_over_the_agreed_boundary_is_blocked()
    {
        WithOwnedRoot(root =>
        {
            // The multibyte prefix is 4,194,302 bytes; three ASCII bytes make
            // the payload 4,194,305 bytes, exactly one over the boundary.
            var contents = new string('é', 2_097_151) + "abc";
            Assert.Equal(AgreedMaximumWriteBytes + 1, Encoding.UTF8.GetByteCount(contents));

            var request = new ToolRequest(
                "write_file",
                "over-boundary.txt",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["contents"] = contents
                },
                Generation);

            var exception = Assert.Throws<ArgumentException>(() => FileActionResolver.Resolve(request, Authority(root)));

            Assert.Contains("exceed", exception.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static TaskAuthority Authority(string root)
    {
        return new TaskAuthority(
            Generation,
            ExplicitUserTask: true,
            AllowedTools: new HashSet<string>(new[] { "write_file" }, StringComparer.Ordinal),
            FileRoot: root);
    }

    private static void WithOwnedRoot(Action<string> test)
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var allowedPrefix = tempRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var root = Path.GetFullPath(Path.Combine(tempRoot, "boo-file-write-boundary-" + Guid.NewGuid().ToString("N")));

        if (!root.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase) ||
            root.Equals(tempRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The boundary fixture path is outside the owned temp directory.");
        }

        Directory.CreateDirectory(root);
        try
        {
            test(root);
        }
        finally
        {
            var resolvedRoot = Path.GetFullPath(root);
            if (!resolvedRoot.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase) ||
                resolvedRoot.Equals(tempRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to clean a path outside the owned temp directory.");
            }

            if (Directory.Exists(resolvedRoot))
            {
                Directory.Delete(resolvedRoot, recursive: true);
            }
        }
    }
}
