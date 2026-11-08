using Boo.Core;
using System.IO;
using Xunit;

namespace Boo.Core.Tests;

public sealed class ActionPolicyTests
{
    private const long Generation = 41;

    // AP-001
    [Fact]
    public void Unknown_tool_is_denied_even_when_the_model_lists_it_as_allowed()
    {
        var request = Request("unknown_tool", "target");
        var authority = Authority("unknown_tool");

        var decision = ActionPolicy.Evaluate(request, authority);

        Assert.Equal(PermissionVerdict.Denied, decision.Verdict);
    }

    // AP-002
    [Fact]
    public void A_request_without_an_explicit_user_task_is_denied()
    {
        var request = Request("search", "design references");
        var authority = Authority("search", explicitUserTask: false);

        var decision = ActionPolicy.Evaluate(request, authority);

        Assert.Equal(PermissionVerdict.Denied, decision.Verdict);
    }

    // AP-003
    [Fact]
    public void A_tool_not_in_the_current_allowlist_is_denied()
    {
        var request = Request("inspect", "fixture window");
        var authority = Authority("search");

        var decision = ActionPolicy.Evaluate(request, authority);

        Assert.Equal(PermissionVerdict.Denied, decision.Verdict);
    }

    // AP-004
    [Fact]
    public void A_stale_generation_is_denied()
    {
        var request = Request("search", "references", generation: Generation - 1);
        var authority = Authority("search");

        var decision = ActionPolicy.Evaluate(request, authority);

        Assert.Equal(PermissionVerdict.Denied, decision.Verdict);
    }

    // AP-005
    [Theory]
    [InlineData("search")]
    [InlineData("inspect")]
    public void Read_only_tools_are_allowed_only_with_explicit_authorization(string tool)
    {
        var request = Request(tool, "fixture");
        var authority = Authority(tool);

        var decision = ActionPolicy.Evaluate(request, authority);

        Assert.Equal(PermissionVerdict.Allowed, decision.Verdict);
    }

    // AP-006
    [Theory]
    [InlineData("http://example.test/docs")]
    [InlineData("https://example.test/docs?q=boo")]
    public void Explicitly_allowlisted_http_urls_are_read_only(string url)
    {
        var request = Request("open_url", url);
        var authority = Authority("open_url");

        var decision = ActionPolicy.Evaluate(request, authority);

        Assert.Equal(PermissionVerdict.Allowed, decision.Verdict);
    }

    // AP-007
    [Theory]
    [InlineData("file:///C:/private.txt")]
    [InlineData("mailto:person@example.test")]
    [InlineData("custom://command")]
    public void Non_http_url_schemes_are_denied_even_with_an_approval_ticket(string url)
    {
        var request = Request("open_url", url);
        var authority = Authority("open_url");

        var decision = ActionPolicy.Evaluate(request, authority, ActionPolicy.Approve(request));

        Assert.Equal(PermissionVerdict.Denied, decision.Verdict);
    }

    // AP-008
    [Theory]
    [InlineData("click")]
    [InlineData("type_text")]
    [InlineData("write_file")]
    [InlineData("delete_file")]
    [InlineData("open_app")]
    public void UI_impacting_and_other_unknown_impact_tools_need_concrete_approval(string tool)
    {
        var request = Request(tool, "fixture-target", ArgumentsFor(tool));
        var authority = Authority(tool);

        var beforeApproval = ActionPolicy.Evaluate(request, authority);
        var afterApproval = ActionPolicy.Evaluate(request, authority, ActionPolicy.Approve(request));

        Assert.Equal(PermissionVerdict.NeedsApproval, beforeApproval.Verdict);
        Assert.Equal(PermissionVerdict.Allowed, afterApproval.Verdict);
    }

    // AP-009
    [Fact]
    public void Changing_the_target_invalidates_a_concrete_approval()
    {
        var request = Request("click", "button-save");
        var authority = Authority("click");
        var approval = ActionPolicy.Approve(request);

        var changed = request with { Target = "button-delete" };

        var decision = ActionPolicy.Evaluate(changed, authority, approval);

        Assert.NotEqual(PermissionVerdict.Allowed, decision.Verdict);
    }

    // AP-010
    [Fact]
    public void Changing_arguments_invalidates_a_concrete_approval()
    {
        var request = Request("type_text", "editor", new Dictionary<string, string>
        {
            ["text"] = "save",
            ["selection"] = "current"
        });
        var authority = Authority("type_text");
        var approval = ActionPolicy.Approve(request);

        var changed = request with
        {
            Arguments = new Dictionary<string, string>
            {
                ["text"] = "delete",
                ["selection"] = "current"
            }
        };

        var decision = ActionPolicy.Evaluate(changed, authority, approval);

        Assert.NotEqual(PermissionVerdict.Allowed, decision.Verdict);
    }

    // AP-011
    [Fact]
    public void Reordering_arguments_does_not_invalidate_a_concrete_approval()
    {
        var request = Request("write_file", "notes.txt", new Dictionary<string, string>
        {
            ["contents"] = "hello",
            ["overwrite"] = "true"
        });
        var reordered = Request("write_file", "notes.txt", new Dictionary<string, string>
        {
            ["overwrite"] = "true",
            ["contents"] = "hello"
        });
        var authority = Authority("write_file");
        var approval = ActionPolicy.Approve(request);

        var decision = ActionPolicy.Evaluate(reordered, authority, approval);

        Assert.Equal(PermissionVerdict.Allowed, decision.Verdict);
    }

    // AP-012
    [Fact]
    public void Changing_the_generation_invalidates_a_concrete_approval()
    {
        var request = Request("delete_file", "old.txt");
        var authority = Authority("delete_file");
        var approval = ActionPolicy.Approve(request);
        var changedAuthority = Authority("delete_file", generation: Generation + 1);
        var changedRequest = request with { Generation = Generation + 1 };

        var decision = ActionPolicy.Evaluate(changedRequest, changedAuthority, approval);

        Assert.NotEqual(PermissionVerdict.Allowed, decision.Verdict);
    }

    private static TaskAuthority Authority(string allowedTool, long generation = Generation, bool explicitUserTask = true)
    {
        return new TaskAuthority(
            generation,
            explicitUserTask,
            new HashSet<string>(new[] { allowedTool }, StringComparer.Ordinal),
            FileRoot: null);
    }

    private static ToolRequest Request(
        string name,
        string target,
        IReadOnlyDictionary<string, string>? arguments = null,
        long generation = Generation)
    {
        return new ToolRequest(name, target, arguments ?? new Dictionary<string, string>(), generation);
    }

    private static IReadOnlyDictionary<string, string> ArgumentsFor(string tool)
    {
        return tool switch
        {
            "type_text" => new Dictionary<string, string> { ["text"] = "hello" },
            "write_file" => new Dictionary<string, string> { ["contents"] = "hello", ["overwrite"] = "true" },
            "delete_file" => new Dictionary<string, string> { ["path"] = "fixture.txt" },
            _ => new Dictionary<string, string>()
        };
    }
}

public sealed class ScopedFilesTests
{
    // SF-001
    [Fact]
    public void Contained_relative_paths_resolve_to_the_canonical_path()
    {
        WithTempRoot(root =>
        {
            var nested = Path.Combine(root, "sub");
            Directory.CreateDirectory(nested);

            var actual = ScopedFiles.Resolve(root, Path.Combine("sub", "note.txt"));

            Assert.Equal(Path.GetFullPath(Path.Combine(nested, "note.txt")), actual);
        });
    }

    // SF-002
    [Fact]
    public void Traversal_is_rejected()
    {
        WithTempRoot(root => AssertRejected(() => ScopedFiles.Resolve(root, Path.Combine("sub", "..", "..", "outside.txt"))));
    }

    // SF-003
    [Fact]
    public void Absolute_paths_are_rejected()
    {
        WithTempRoot(root =>
        {
            var outside = Path.Combine(Path.GetDirectoryName(root)!, "outside.txt");
            AssertRejected(() => ScopedFiles.Resolve(root, outside));
        });
    }

    // SF-004
    [Fact]
    public void Sibling_prefix_escapes_are_rejected()
    {
        WithTempRoot(root =>
        {
            var sibling = root + "-sibling";
            AssertRejected(() => ScopedFiles.Resolve(root, Path.Combine(sibling, "secret.txt")));
        });
    }

    // SF-005
    [Fact]
    public void Alternate_data_streams_are_rejected()
    {
        WithTempRoot(root => AssertRejected(() => ScopedFiles.Resolve(root, "notes.txt:secret")));
    }

    // SF-006
    [Fact(Skip = "Pending native acceptance: this host did not provide a reparse-capable disposable fixture without changing system settings.")]
    public void Reparse_point_escapes_are_rejected_when_the_host_allows_a_fixture_link()
    {
        WithTempRoot(root =>
        {
            var parent = Path.GetDirectoryName(root)!;
            var outside = Path.Combine(parent, "boo-reparse-outside-" + Guid.NewGuid().ToString("N"));
            var link = Path.Combine(root, "link");
            var directoryLink = false;
            var linkCreated = false;

            try
            {
                Directory.CreateDirectory(outside);
                File.WriteAllText(Path.Combine(outside, "secret.txt"), "fixture");

                try
                {
                    Directory.CreateSymbolicLink(link, outside);
                    directoryLink = true;
                    linkCreated = true;
                }
                catch (UnauthorizedAccessException)
                {
                    // A file link may be available even when directory links
                    // are restricted by the host's current Windows policy.
                }
                catch (IOException)
                {
                    // Fall through to the file-link attempt.
                }
                catch (PlatformNotSupportedException)
                {
                    // The test is pending on a host without link support.
                }

                if (!linkCreated)
                {
                    var outsideFile = Path.Combine(outside, "file-secret.txt");
                    File.WriteAllText(outsideFile, "fixture");
                    try
                    {
                        File.CreateSymbolicLink(link, outsideFile);
                        linkCreated = true;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return;
                    }
                    catch (IOException)
                    {
                        return;
                    }
                    catch (PlatformNotSupportedException)
                    {
                        return;
                    }
                }

                var candidate = directoryLink ? Path.Combine("link", "secret.txt") : "link";
                AssertRejected(() => ScopedFiles.Resolve(root, candidate));
            }
            finally
            {
                if (Directory.Exists(outside))
                {
                    Directory.Delete(outside, recursive: true);
                }
            }
        });
    }

    private static void WithTempRoot(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "boo-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            test(root);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void AssertRejected(Action action)
    {
        var exception = Record.Exception(action);

        Assert.NotNull(exception);
        Assert.False(exception is NotImplementedException, "The initial stub must remain a RED failure, not a passing rejection.");
    }
}
