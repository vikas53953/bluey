using Boo.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Boo.Core.Tests;

public sealed class ActionPreviewValidationTests
{
    private const long Generation = 91;

    [Fact]
    public void Type_text_preview_preserves_multiline_empty_and_long_argument_values()
    {
        const string multiline = "first line\r\nsecond line\nthird line";
        const string empty = "";
        var longValue = new string('x', 8192);
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["z-long"] = longValue,
            ["empty"] = empty,
            ["text"] = multiline
        };
        var request = new ToolRequest("type_text", "editor", arguments, Generation);

        var preview = ActionPreviewBuilder.Create(request, Authority("type_text"));

        Assert.True(preview.CanApprove);
        Assert.Equal(new[] { "empty", "text", "z-long" }, preview.Fields.Select(field => field.Key));
        Assert.Equal(empty, Field(preview, "empty").Value);
        Assert.Equal("(empty value)", Field(preview, "empty").DisplayValue);
        Assert.Equal(multiline, Field(preview, "text").Value);
        Assert.Equal(multiline, Field(preview, "text").DisplayValue);
        Assert.Equal(longValue, Field(preview, "z-long").Value);
        Assert.Equal(longValue, Field(preview, "z-long").DisplayValue);
    }

    [Fact]
    public void Denied_authority_disables_the_preview_with_a_reason()
    {
        var authority = new TaskAuthority(
            Generation,
            ExplicitUserTask: true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal),
            FileRoot: null);

        var preview = ActionPreviewBuilder.Create(
            new ToolRequest("type_text", "editor", new Dictionary<string, string>(), Generation),
            authority);

        Assert.False(preview.CanApprove);
        Assert.False(string.IsNullOrWhiteSpace(preview.BlockedReason));
    }

    [Fact]
    public void Stale_generation_disables_the_preview()
    {
        var request = new ToolRequest("type_text", "editor", new Dictionary<string, string>(), Generation - 1);

        var preview = ActionPreviewBuilder.Create(request, Authority("type_text"));

        Assert.False(preview.CanApprove);
        Assert.False(string.IsNullOrWhiteSpace(preview.BlockedReason));
    }

    [Fact]
    public void Unsupported_action_is_disabled_and_has_an_unsupported_title()
    {
        var request = new ToolRequest("not_a_tool", "fixture", new Dictionary<string, string>(), Generation);

        var preview = ActionPreviewBuilder.Create(request, Authority("not_a_tool"));

        Assert.Equal("Unsupported action", preview.Title);
        Assert.False(preview.CanApprove);
        Assert.False(string.IsNullOrWhiteSpace(preview.BlockedReason));
    }

    [Fact]
    public void Contents_takes_precedence_over_content_even_when_explicit_contents_is_empty()
    {
        WithOwnedRoot(root =>
        {
            var authority = Authority("write_file", fileRoot: root);
            var preferred = new ToolRequest(
                "write_file",
                "preferred.txt",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["content"] = "alias value",
                    ["contents"] = "primary value",
                    ["overwrite"] = "false"
                },
                Generation);

            var preferredPlan = FileActionResolver.Resolve(preferred, authority);

            Assert.Equal("primary value", preferredPlan.Contents);

            var explicitEmpty = preferred with
            {
                Arguments = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["content"] = "alias value",
                    ["contents"] = "",
                    ["overwrite"] = "false"
                }
            };
            var emptyPlan = FileActionResolver.Resolve(explicitEmpty, authority);
            var emptyPreview = ActionPreviewBuilder.Create(explicitEmpty, authority);

            Assert.Equal("", emptyPlan.Contents);
            Assert.True(emptyPreview.CanApprove);
            Assert.Equal(
                "File content (unused alias; contents takes precedence)",
                Field(emptyPreview, "content").Label);
        });
    }

    [Fact]
    public void Blank_path_falls_back_to_target_and_nonblank_path_is_the_canonical_override()
    {
        WithOwnedRoot(root =>
        {
            var nested = Path.Combine(root, "nested");
            Directory.CreateDirectory(nested);
            var authority = Authority("write_file", fileRoot: root);

            var fallback = new ToolRequest(
                "write_file",
                "target.txt",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["path"] = "  ",
                    ["contents"] = "fallback",
                    ["overwrite"] = "false"
                },
                Generation);
            var overrideRequest = new ToolRequest(
                "write_file",
                "raw-target.txt",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["path"] = Path.Combine("nested", "override.txt"),
                    ["contents"] = "override",
                    ["overwrite"] = "false"
                },
                Generation);

            var fallbackPlan = FileActionResolver.Resolve(fallback, authority);
            var overridePlan = FileActionResolver.Resolve(overrideRequest, authority);

            Assert.Equal(Path.GetFullPath(Path.Combine(root, "target.txt")), fallbackPlan.CanonicalPath);
            Assert.Equal(Path.GetFullPath(Path.Combine(nested, "override.txt")), overridePlan.CanonicalPath);
            Assert.Equal(
                overridePlan.CanonicalPath,
                ActionPreviewBuilder.Create(overrideRequest, authority).Destination);
        });
    }

    [Fact]
    public void Missing_root_disables_preview_and_resolver_rejects_it()
    {
        var request = WriteRequest("target.txt", new Dictionary<string, string>
        {
            ["contents"] = "value"
        });
        var authority = Authority("write_file", fileRoot: null);

        var exception = Assert.Throws<ArgumentException>(() => FileActionResolver.Resolve(request, authority));
        var preview = ActionPreviewBuilder.Create(request, authority);

        Assert.Contains("root", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(preview.CanApprove);
        Assert.False(string.IsNullOrWhiteSpace(preview.BlockedReason));
    }

    [Fact]
    public void Missing_parent_is_rejected_and_missing_contents_is_blocked()
    {
        WithOwnedRoot(root =>
        {
            var authority = Authority("write_file", fileRoot: root);
            var missingParent = WriteRequest(
                Path.Combine("missing-parent", "target.txt"),
                new Dictionary<string, string> { ["contents"] = "value" });
            var missingContents = WriteRequest("target.txt", new Dictionary<string, string>());

            Assert.Throws<DirectoryNotFoundException>(() => FileActionResolver.Resolve(missingParent, authority));
            var exception = Assert.Throws<ArgumentException>(() => FileActionResolver.Resolve(missingContents, authority));
            var preview = ActionPreviewBuilder.Create(missingContents, authority);

            Assert.Contains("contents", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(preview.CanApprove);
            Assert.Contains("contents", preview.BlockedReason!, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Oversize_utf8_contents_are_rejected_before_a_write_plan_is_created()
    {
        WithOwnedRoot(root =>
        {
            var oversized = new string('é', FileActionResolver.MaximumWriteBytes / 2 + 1);
            var request = WriteRequest("oversized.txt", new Dictionary<string, string>
            {
                ["contents"] = oversized
            });
            var authority = Authority("write_file", fileRoot: root);

            var exception = Assert.Throws<ArgumentException>(() => FileActionResolver.Resolve(request, authority));
            var preview = ActionPreviewBuilder.Create(request, authority);

            Assert.Contains("exceed", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(preview.CanApprove);
            Assert.Contains("exceed", preview.BlockedReason!, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Malformed_overwrite_option_is_rejected()
    {
        WithOwnedRoot(root =>
        {
            var request = WriteRequest("malformed.txt", new Dictionary<string, string>
            {
                ["contents"] = "value",
                ["overwrite"] = "sometimes"
            });

            var exception = Assert.Throws<ArgumentException>(
                () => FileActionResolver.Resolve(request, Authority("write_file", fileRoot: root)));

            Assert.Contains("overwrite", exception.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Existing_file_requires_explicit_overwrite()
    {
        WithOwnedRoot(root =>
        {
            var target = Path.Combine(root, "existing.txt");
            File.WriteAllText(target, "original");
            var request = WriteRequest("existing.txt", new Dictionary<string, string>
            {
                ["contents"] = "replacement",
                ["overwrite"] = "false"
            });
            var authority = Authority("write_file", fileRoot: root);

            var exception = Assert.Throws<IOException>(() => FileActionResolver.Resolve(request, authority));
            var preview = ActionPreviewBuilder.Create(request, authority);

            Assert.Contains("overwrite", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(preview.CanApprove);
            Assert.False(string.IsNullOrWhiteSpace(preview.BlockedReason));
        });
    }

    [Fact]
    public void Regular_write_and_delete_requests_produce_expected_plans()
    {
        WithOwnedRoot(root =>
        {
            var authority = Authority("write_file", fileRoot: root);
            var contents = "line one\nline two";
            var writeRequest = WriteRequest("new.txt", new Dictionary<string, string>
            {
                ["contents"] = contents,
                ["overwrite"] = "false"
            });

            var writePlan = FileActionResolver.Resolve(writeRequest, authority);

            Assert.Equal(Path.GetFullPath(Path.Combine(root, "new.txt")), writePlan.CanonicalPath);
            Assert.Equal(contents, writePlan.Contents);
            Assert.False(writePlan.Overwrite);

            var deleteTarget = Path.Combine(root, "delete.txt");
            File.WriteAllText(deleteTarget, "fixture");
            var deleteRequest = new ToolRequest(
                "delete_file",
                "delete.txt",
                new Dictionary<string, string>(),
                Generation);
            var deletePlan = FileActionResolver.Resolve(deleteRequest, Authority("delete_file", fileRoot: root));

            Assert.Equal(deleteTarget, deletePlan.CanonicalPath);
            Assert.Null(deletePlan.Contents);
            Assert.False(deletePlan.Overwrite);
        });
    }

    [Fact]
    public void Directory_and_missing_delete_targets_are_rejected()
    {
        WithOwnedRoot(root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "directory-target"));
            var directoryRequest = new ToolRequest(
                "delete_file",
                "directory-target",
                new Dictionary<string, string>(),
                Generation);
            var missingRequest = new ToolRequest(
                "delete_file",
                "missing.txt",
                new Dictionary<string, string>(),
                Generation);
            var authority = Authority("delete_file", fileRoot: root);

            Assert.Throws<FileNotFoundException>(() => FileActionResolver.Resolve(directoryRequest, authority));
            Assert.Throws<FileNotFoundException>(() => FileActionResolver.Resolve(missingRequest, authority));
        });
    }

    [Fact]
    public void Traversal_is_rejected_for_file_actions()
    {
        WithOwnedRoot(root =>
        {
            var request = WriteRequest(Path.Combine("..", "outside.txt"), new Dictionary<string, string>
            {
                ["contents"] = "must not escape"
            });

            var exception = Assert.Throws<ArgumentException>(
                () => FileActionResolver.Resolve(request, Authority("write_file", fileRoot: root)));

            Assert.Contains("path", exception.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Read_only_preview_leaves_the_input_request_and_arguments_unchanged()
    {
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["query"] = "unchanged query",
            ["empty"] = ""
        };
        var request = new ToolRequest("search", "references", arguments, Generation);

        var preview = ActionPreviewBuilder.Create(request, Authority("search"));

        Assert.True(preview.CanApprove);
        Assert.Same(arguments, request.Arguments);
        Assert.Equal("unchanged query", request.Arguments["query"]);
        Assert.Equal("", request.Arguments["empty"]);
        Assert.Equal("unchanged query", Field(preview, "query").Value);
        Assert.Equal("(empty value)", Field(preview, "empty").DisplayValue);
    }

    private static ActionPreviewField Field(ActionPreviewModel preview, string key)
    {
        return Assert.Single(preview.Fields.Where(field => field.Key == key));
    }

    private static ToolRequest WriteRequest(string target, IReadOnlyDictionary<string, string> arguments)
    {
        return new ToolRequest("write_file", target, arguments, Generation);
    }

    private static TaskAuthority Authority(string tool, string? fileRoot = null, long generation = Generation)
    {
        return new TaskAuthority(
            generation,
            ExplicitUserTask: true,
            AllowedTools: new HashSet<string>(new[] { tool }, StringComparer.Ordinal),
            FileRoot: fileRoot);
    }

    private static void WithOwnedRoot(Action<string> test)
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var allowedPrefix = tempRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var root = Path.GetFullPath(Path.Combine(tempRoot, "boo-action-validation-" + Guid.NewGuid().ToString("N")));

        if (!root.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase) ||
            root.Equals(tempRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The validation fixture path is outside the owned temp directory.");
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
