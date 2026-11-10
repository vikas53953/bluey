using Boo.Core;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Boo.Core.Tests;

public enum ReparseFixtureKind
{
    Junction,
    DirectorySymlink,
    FileSymlink,
}

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

    // SF-006: a reparse point inside the root (directory junction, directory symlink or file symlink)
    // must never let a scoped path reach a file outside it. A junction needs no privilege on NTFS, so
    // that case always runs on Windows. Symbolic links need the create-symlink privilege (an elevated
    // shell or Windows Developer Mode); without it those cases FAIL with instructions. This test never
    // skips and never returns early: an untested escape guard is reported, not hidden.
    [Theory]
    [InlineData(ReparseFixtureKind.Junction)]
    [InlineData(ReparseFixtureKind.DirectorySymlink)]
    [InlineData(ReparseFixtureKind.FileSymlink)]
    public void Reparse_point_escapes_are_rejected(ReparseFixtureKind kind)
    {
        WithTempRoot(root =>
        {
            var outside = Path.Combine(Path.GetDirectoryName(root)!, "boo-reparse-outside-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(outside);
                var outsideFile = Path.Combine(outside, "secret.txt");
                File.WriteAllText(outsideFile, "outside");

                // Control: the same shape through an ordinary folder resolves, so the rejection
                // asserted below can only come from the reparse point.
                Directory.CreateDirectory(Path.Combine(root, "real"));
                File.WriteAllText(Path.Combine(root, "real", "secret.txt"), "inside");
                Assert.Equal(Path.GetFullPath(Path.Combine(root, "real", "secret.txt")), ScopedFiles.Resolve(root, Path.Combine("real", "secret.txt")));

                var link = Path.Combine(root, "link");
                string candidate;
                switch (kind)
                {
                    case ReparseFixtureKind.Junction:
                        CreateJunction(link, outside);
                        candidate = Path.Combine("link", "secret.txt");
                        break;
                    case ReparseFixtureKind.DirectorySymlink:
                        CreateSymbolicLink(() => Directory.CreateSymbolicLink(link, outside));
                        candidate = Path.Combine("link", "secret.txt");
                        break;
                    default:
                        CreateSymbolicLink(() => File.CreateSymbolicLink(link, outsideFile));
                        candidate = "link";
                        break;
                }

                // The fixture really escapes: reading through the link reaches the outside file.
                Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint), $"the {kind} fixture is not a reparse point");
                Assert.Equal("outside", File.ReadAllText(Path.Combine(root, candidate)));

                var exception = Assert.ThrowsAny<IOException>(() => ScopedFiles.Resolve(root, candidate));
                Assert.Contains("reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
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

    private static void CreateJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            // Junctions are an NTFS feature; elsewhere a directory symlink is the equivalent and needs no privilege.
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        var start = new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("cmd.exe did not start");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(link))
        {
            Assert.Fail($"SF-006 could not create its directory-junction fixture (mklink /J exit {process.ExitCode}): {output.Trim()}");
        }
    }

    private static void CreateSymbolicLink(Action create)
    {
        try
        {
            create();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            Assert.Fail("SF-006 must create a symbolic link and this machine refused (" + exception.Message + "). "
                + "Turn on Windows Developer Mode (Settings > System > For developers) or run the tests elevated. "
                + "This case is never skipped.");
        }
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
