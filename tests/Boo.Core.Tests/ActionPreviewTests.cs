using Boo.Core;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Boo.Core.Tests;

public sealed class ActionPreviewTests
{
    private const long Generation = 73;

    // APV-001
    [Fact]
    public void Write_preview_keeps_the_raw_target_full_arguments_and_an_immutable_request_copy()
    {
        WithOwnedTempRoot(root =>
        {
            var parent = Path.Combine(root, "existing-parent");
            Directory.CreateDirectory(parent);

            const string rawTarget = "the-target-shown-to-the-user.txt";
            var relativeOverride = Path.Combine("existing-parent", "actual-destination.txt");
            var expectedDestination = Path.GetFullPath(Path.Combine(root, relativeOverride));
            const string multilineContents = "first line\r\nsecond line\nthird line";
            var originalArguments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["overwrite"] = "true",
                ["contents"] = multilineContents,
                ["path"] = relativeOverride
            };
            var request = new ToolRequest("write_file", rawTarget, originalArguments, Generation);
            var authority = new TaskAuthority(
                Generation,
                ExplicitUserTask: true,
                AllowedTools: new HashSet<string>(new[] { "write_file" }, StringComparer.Ordinal),
                FileRoot: root);

            var preview = ActionPreviewBuilder.Create(request, authority);

            Assert.True(preview.CanApprove);
            Assert.Null(preview.BlockedReason);
            Assert.Equal(rawTarget, preview.RequestedTarget);
            Assert.Equal(expectedDestination, preview.Destination);
            Assert.Equal(
                new[] { "contents", "overwrite", "path" },
                preview.Fields.Select(field => field.Key));

            Assert.Equal(multilineContents, Field(preview, "contents").Value);
            Assert.Equal("true", Field(preview, "overwrite").Value);
            Assert.Equal(relativeOverride, Field(preview, "path").Value);
            Assert.Equal(multilineContents, Field(preview, "contents").DisplayValue);

            Assert.NotSame(request, preview.Request);
            Assert.Equal(rawTarget, preview.Request.Target);
            Assert.Equal(relativeOverride, preview.Request.Arguments["path"]);

            originalArguments["path"] = "changed-after-preview.txt";
            originalArguments["contents"] = "changed after preview";
            originalArguments["overwrite"] = "false";
            originalArguments["new-key"] = "must not appear";

            Assert.Equal(multilineContents, Field(preview, "contents").Value);
            Assert.Equal("true", Field(preview, "overwrite").Value);
            Assert.Equal(relativeOverride, Field(preview, "path").Value);
            Assert.Equal(relativeOverride, preview.Request.Arguments["path"]);
            Assert.Equal(multilineContents, preview.Request.Arguments["contents"]);
            Assert.Equal("true", preview.Request.Arguments["overwrite"]);
            Assert.False(preview.Request.Arguments.ContainsKey("new-key"));
        });
    }

    private static ActionPreviewField Field(ActionPreviewModel preview, string key)
    {
        return Assert.Single(preview.Fields.Where(field => field.Key == key));
    }

    private static void WithOwnedTempRoot(Action<string> test)
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var root = Path.GetFullPath(Path.Combine(tempRoot, "boo-action-preview-" + Guid.NewGuid().ToString("N")));
        var allowedPrefix = tempRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        Assert.StartsWith(allowedPrefix, root, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(tempRoot, root, StringComparer.OrdinalIgnoreCase);

        Directory.CreateDirectory(root);
        try
        {
            test(root);
        }
        finally
        {
            var resolvedRoot = Path.GetFullPath(root);
            Assert.StartsWith(allowedPrefix, resolvedRoot, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(tempRoot, resolvedRoot, StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(resolvedRoot))
            {
                Directory.Delete(resolvedRoot, recursive: true);
            }
        }
    }
}
