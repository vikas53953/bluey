using System.Globalization;
using System.Text.Json;

namespace Boo.Core.Tests;

/// <summary>
/// Property-style fuzz tests for everything in Boo.Core that reads untrusted input: model tool arguments (voice
/// commands), provider citations (research sources), the action boundary (policy, URLs, approval digests, scoped
/// files) and annotation proposals. Each property runs thousands of seeded cases; the seed is in every failure.
/// </summary>
public sealed class UntrustedInputFuzzTests
{
    private static IEnumerable<FuzzKit> Kits(int count = 8) => Enumerable.Range(1, count).Select(seed => new FuzzKit(seed * 7919));

    // ---- Voice commands -------------------------------------------------------------------------------------

    [Fact]
    public void Voice_parser_never_throws_and_always_yields_a_clean_bounded_command()
    {
        foreach (var kit in Kits())
        {
            for (var i = 0; i < FuzzKit.Iterations; i++)
            {
                var toolName = kit.Random.Next(3) == 0 ? kit.MaybeText(12) : kit.Pick(VoiceCommandParser.ToolNames);
                var arguments = kit.Random.Next(4) switch
                {
                    0 => kit.MaybeText(200),
                    1 => kit.Json(),
                    2 => "{\"task\":" + JsonSerializer.Serialize(kit.Text(300)) + ",\"minutes\":" + kit.Json(3) + "}",
                    _ => "{\"task\":" + kit.Json(2) + "}",
                };

                bool ok;
                VoiceCommand? command;
                string? error;
                try
                {
                    ok = VoiceCommandParser.TryParse(toolName, arguments, out command, out error);
                }
                catch (Exception ex)
                {
                    throw new Xunit.Sdk.XunitException($"TryParse threw {ex.GetType().Name}: {ex.Message} ({kit.Describe(toolName + " | " + arguments)})");
                }

                if (!ok)
                {
                    Assert.Null(command);
                    Assert.False(string.IsNullOrWhiteSpace(error), kit.Describe(arguments ?? "null"));
                    Assert.DoesNotContain(error!, c => char.IsControl(c));
                    continue;
                }

                Assert.NotNull(command);
                Assert.Null(error);
                Assert.True(VoiceCommandParser.IsVoiceTool(toolName));
                if (command!.Kind != VoiceCommandKind.FocusStart)
                    continue;

                var task = command.Task!;
                Assert.InRange(task.Length, 1, VoiceCommandParser.MaxTaskLength);
                Assert.Equal(task, task.Trim());
                Assert.DoesNotContain(task, c => char.IsControl(c));
                Assert.DoesNotContain("  ", task, StringComparison.Ordinal);
                Assert.False(char.IsHighSurrogate(task[^1]), kit.Describe(arguments!));
                Assert.True(double.IsFinite(command.Minutes));
                Assert.InRange(command.Minutes, VoiceCommandParser.MinMinutes, VoiceCommandParser.MaxMinutes);

                // The spoken reply must also be well-formed for whatever the parser accepted.
                var decision = VoiceFocusPolicy.Decide(command, null, kit.Double());
                Assert.True(decision.Apply);
                Assert.DoesNotContain(decision.Reply, c => char.IsControl(c));
            }
        }
    }

    // ---- Research sources -----------------------------------------------------------------------------------

    [Fact]
    public void Research_sources_are_bounded_http_only_deduplicated_and_free_of_control_characters()
    {
        foreach (var kit in Kits())
        {
            for (var i = 0; i < FuzzKit.Iterations / 4; i++)
            {
                var count = kit.Random.Next(0, 40);
                var candidates = new List<SourceLink?>();
                for (var c = 0; c < count; c++)
                {
                    if (kit.Random.Next(10) == 0) { candidates.Add(null); continue; }
                    var url = kit.Random.Next(3) switch
                    {
                        0 => kit.Text(40),
                        1 => "https://" + kit.Text(6) + "example.com/" + kit.Text(10) + "?" + kit.Text(8),
                        _ => kit.Pick(KnownUrls),
                    };
                    candidates.Add(new SourceLink(kit.MaybeText(200) ?? string.Empty, url));
                }

                var sources = ResearchSources.Normalize(candidates);
                Assert.InRange(sources.Count, 0, ResearchSources.MaximumSources);
                Assert.Equal(sources.Count, sources.Select(s => s.Url).Distinct(StringComparer.Ordinal).Count());
                foreach (var source in sources)
                {
                    var uri = new Uri(source.Url, UriKind.Absolute);
                    Assert.True(uri.Scheme is "http" or "https", kit.Describe(source.Url));
                    Assert.Equal(string.Empty, uri.UserInfo);
                    Assert.Equal(uri.Host, uri.Host.ToLowerInvariant());
                    Assert.DoesNotContain(source.Url, c => char.IsControl(c) || char.IsWhiteSpace(c));
                    Assert.DoesNotContain(source.Title, c => char.IsControl(c));
                    Assert.InRange(source.Title.Length, 1, ResearchSources.MaximumTitleLength);
                    Assert.DoesNotContain(uri.Query.TrimStart('?').Split('&'), p => p.StartsWith("utm_", StringComparison.OrdinalIgnoreCase));
                    Assert.True(ActionPolicy.IsSafeHttpUrl(source.Url), kit.Describe(source.Url));
                }

                var result = ResearchSources.Finalize("answer", candidates);
                Assert.Equal(sources.Count > 0, result.Sourced);
            }
        }
    }

    [Fact]
    public void Research_extract_survives_arbitrary_response_shapes()
    {
        foreach (var kit in Kits())
        {
            for (var i = 0; i < FuzzKit.Iterations / 4; i++)
            {
                var json = kit.Random.Next(2) == 0
                    ? kit.Json()
                    : "{\"output\":[{\"type\":" + kit.Json(3) + ",\"content\":[{\"annotations\":[{\"type\":\"url_citation\",\"url\":" + kit.Json(3) + ",\"title\":" + kit.Json(3) + "}]}],\"action\":{\"sources\":[{\"url\":" + kit.Json(3) + "}]}}]}";
                JsonDocument document;
                try { document = JsonDocument.Parse(json); }
                catch (Exception ex) when (ex is JsonException or ArgumentException) { continue; }
                using (document)
                {
                    var sources = ResearchSources.Extract(document.RootElement);
                    Assert.InRange(sources.Count, 0, ResearchSources.MaximumSources);
                    Assert.All(sources, s => Assert.True(ActionPolicy.IsSafeHttpUrl(s.Url), kit.Describe(json)));
                }
            }
        }
    }

    private static readonly string[] KnownUrls =
    {
        "https://example.com/a?utm_source=x&b=1", "HTTPS://EXAMPLE.COM/a?b=1", "http://user@example.com/",
        "https://bank.example@evil.test/", "ftp://example.com/x", "javascript:alert(1)", "file:///etc/passwd",
        "https://example.com:8443/path", "https://example.com:443/path", "https://xn--e1afmkfd.xn--p1ai/",
        "https://ex ample.com/", "https://example.com/\u0000", " https://example.com/trim ", "https:///nohost",
        "https://[::1]/", "https://127.0.0.1/", "http://example.com/" + new string('a', 70_000),
    };

    // ---- Action policy --------------------------------------------------------------------------------------

    [Fact]
    public void Action_policy_only_allows_catalog_tools_on_the_live_generation_with_a_matching_approval()
    {
        var tools = ActionPolicy.ToolNames.ToArray();
        foreach (var kit in Kits())
        {
            for (var i = 0; i < FuzzKit.Iterations / 2; i++)
            {
                var name = kit.Random.Next(4) == 0 ? kit.Text(10) : kit.Pick(tools);
                var generation = kit.Long();
                var arguments = Enumerable.Range(0, kit.Random.Next(0, 5))
                    .Select(_ => kit.Key())
                    .Distinct(StringComparer.Ordinal)
                    .ToDictionary(k => k, _ => kit.Text(20), StringComparer.Ordinal);
                var request = new ToolRequest(name, kit.Text(30), arguments, generation);
                var allowed = kit.Random.Next(5) == 0 ? null : tools.Where(_ => kit.Random.Next(2) == 0).ToHashSet(StringComparer.Ordinal);
                var authority = new TaskAuthority(kit.Random.Next(3) == 0 ? kit.Long() : generation, kit.Random.Next(4) != 0, allowed!);
                var approval = kit.Random.Next(3) switch
                {
                    0 => null,
                    1 => ActionPolicy.Approve(request),
                    _ => new ApprovalTicket(kit.Long(), kit.Random.Next(2) == 0 ? kit.Text(64) : ActionPolicy.ComputeDigest(request)),
                };

                var decision = ActionPolicy.Evaluate(request, authority, approval);
                var inCatalog = ActionPolicy.ToolNames.Contains(name);
                var authorised = inCatalog && authority.ExplicitUserTask && authority.Generation == generation &&
                                 allowed is not null && allowed.Contains(name);
                if (!authorised)
                {
                    Assert.Equal(PermissionVerdict.Denied, decision.Verdict);
                    continue;
                }

                if (name == "open_url" && !ActionPolicy.IsSafeHttpUrl(request.Target))
                {
                    Assert.Equal(PermissionVerdict.Denied, decision.Verdict);
                    continue;
                }

                if (name is "search" or "inspect" or "open_url")
                {
                    Assert.Equal(PermissionVerdict.Allowed, decision.Verdict);
                    continue;
                }

                var exact = approval is not null && approval.Generation == generation &&
                            approval.Digest == ActionPolicy.ComputeDigest(request);
                Assert.Equal(approval is null ? PermissionVerdict.NeedsApproval : exact ? PermissionVerdict.Allowed : PermissionVerdict.Denied, decision.Verdict);
            }
        }
    }

    [Fact]
    public void Approval_digest_changes_when_any_field_changes_and_ignores_argument_order()
    {
        foreach (var kit in Kits())
        {
            for (var i = 0; i < FuzzKit.Iterations / 2; i++)
            {
                var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
                for (var a = 0; a < kit.Random.Next(1, 5); a++) arguments[kit.Key() + a] = kit.Text(12);
                var request = new ToolRequest(kit.Pick(ActionPolicy.ToolNames.ToArray()), kit.Text(12), arguments, kit.Long());
                var digest = ActionPolicy.ComputeDigest(request);

                var reversed = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var pair in arguments.Reverse()) reversed[pair.Key] = pair.Value;
                Assert.Equal(digest, ActionPolicy.ComputeDigest(request with { Arguments = reversed }));

                Assert.NotEqual(digest, ActionPolicy.ComputeDigest(request with { Target = request.Target + "x" }));
                Assert.NotEqual(digest, ActionPolicy.ComputeDigest(request with { Generation = unchecked(request.Generation + 1) }));
                var key = arguments.Keys.First();
                var mutated = new Dictionary<string, string>(arguments, StringComparer.Ordinal) { [key] = arguments[key] + "|" };
                Assert.NotEqual(digest, ActionPolicy.ComputeDigest(request with { Arguments = mutated }));
                // Moving a boundary between key and value must not collide ("ab"+"c" vs "a"+"bc").
                if (arguments[key].Length == 0) continue;
                var shifted = new Dictionary<string, string>(StringComparer.Ordinal) { [key + arguments[key]] = string.Empty };
                foreach (var pair in arguments.Where(p => p.Key != key)) shifted[pair.Key] = pair.Value;
                Assert.NotEqual(digest, ActionPolicy.ComputeDigest(request with { Arguments = shifted }));
            }
        }
    }

    [Fact]
    public void Safe_http_url_rejects_everything_that_is_not_a_plain_absolute_http_url()
    {
        foreach (var kit in Kits())
        {
            for (var i = 0; i < FuzzKit.Iterations; i++)
            {
                var value = kit.Random.Next(3) == 0 ? kit.Pick(KnownUrls) : kit.Random.Next(2) == 0 ? kit.Text(30) : "https://" + kit.Text(20);
                bool safe;
                try { safe = ActionPolicy.IsSafeHttpUrl(value); }
                catch (Exception ex) { throw new Xunit.Sdk.XunitException($"IsSafeHttpUrl threw {ex.GetType().Name} ({kit.Describe(value)})"); }
                if (!safe) continue;
                var uri = new Uri(value, UriKind.Absolute);
                Assert.True(uri.Scheme is "http" or "https", kit.Describe(value));
                Assert.False(string.IsNullOrWhiteSpace(uri.Host));
                Assert.Equal(string.Empty, uri.UserInfo);
                Assert.DoesNotContain(value, char.IsControl);
            }
        }
    }

    // ---- Scoped files ---------------------------------------------------------------------------------------

    [Fact]
    public void Scoped_files_never_resolve_outside_the_root()
    {
        var root = Directory.CreateTempSubdirectory("boo-fuzz-root").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(root, "sub", "file.txt"), "x");
            var rootPrefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var kit in Kits())
            {
                for (var i = 0; i < FuzzKit.Iterations / 2; i++)
                {
                    var relative = kit.Random.Next(3) switch
                    {
                        0 => kit.Text(12),
                        1 => string.Join(kit.Random.Next(2) == 0 ? "/" : "\\", Enumerable.Range(0, kit.Random.Next(1, 5)).Select(_ => kit.Pick(Segments))),
                        _ => kit.Pick(Segments) + kit.Text(5),
                    };
                    string resolved;
                    try { resolved = ScopedFiles.Resolve(root, relative); }
                    catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or IOException or NotSupportedException)
                    { continue; }
                    catch (Exception ex)
                    { throw new Xunit.Sdk.XunitException($"Resolve threw {ex.GetType().Name}: {ex.Message} ({kit.Describe(relative)})"); }

                    Assert.StartsWith(rootPrefix, resolved, StringComparison.OrdinalIgnoreCase);
                    Assert.True(resolved.Length > rootPrefix.Length, kit.Describe(relative));
                    Assert.DoesNotContain(Path.GetRelativePath(root, resolved).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), s => s is "." or "..");
                    Assert.DoesNotContain(':', relative);
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static readonly string[] Segments =
    {
        "sub", "file.txt", "..", ".", "...", "..\\", "../", "%2e%2e", "C:", "C:\\Windows", "\\\\server\\share", "\\\\?\\C:\\x",
        "file.txt:stream", "con", "nul", "aux", " ", "", "a b", "\u0000", "sub\u0000/../..", "~", "$HOME", "sub/./../sub",
    };

    // ---- Annotation proposals -------------------------------------------------------------------------------

    [Fact]
    public void Annotation_validator_accepts_only_finite_in_frame_marks_of_known_kinds()
    {
        foreach (var kit in Kits())
        {
            for (var i = 0; i < FuzzKit.Iterations / 2; i++)
            {
                var now = DateTimeOffset.UnixEpoch.AddDays(20_000);
                var width = kit.Random.Next(4) == 0 ? kit.Random.Next(-5, 2) : kit.Random.Next(1, 4000);
                var height = kit.Random.Next(4) == 0 ? kit.Random.Next(-5, 2) : kit.Random.Next(1, 4000);
                var context = new ScreenContext(Guid.NewGuid(), kit.Long(),
                    new DisplayInfo("d", "D", new RectD(0, 0, 1920, 1080), 1), width, height,
                    new WindowIdentity(kit.Random.Next(3) == 0 ? 0 : 7, kit.Random.Next(3) == 0 ? 0 : 9, kit.Random.Next(5) == 0 ? " " : "proc", new RectD(0, 0, 10, 10)),
                    now.AddSeconds(-kit.Random.Next(-3, 40)));
                var marks = Enumerable.Range(0, kit.Random.Next(0, 6)).Select(_ => kit.Random.Next(12) == 0 ? null! :
                    new AnnotationMark(kit.Random.Next(3) == 0 ? kit.Text(6) : kit.Pick(Kinds),
                        new RectD(kit.Double(), kit.Double(), kit.Double(), kit.Double()), kit.Text(20), kit.Double(), kit.Double())).ToList();
                var proposal = new AnnotationProposal(
                    kit.Random.Next(6) == 0 ? Guid.NewGuid() : context.ObservationId,
                    kit.Random.Next(6) == 0 ? kit.Long() : context.Generation,
                    kit.Random.Next(6) == 0 ? kit.Text(3) : "d", marks);

                var result = AnnotationValidator.Validate(proposal, context, now);
                if (!result.Valid)
                {
                    Assert.False(string.IsNullOrWhiteSpace(result.Reason));
                    continue;
                }

                Assert.Equal(context.ObservationId, proposal.ObservationId);
                Assert.Equal(context.Generation, proposal.Generation);
                Assert.NotEmpty(marks);
                foreach (var mark in marks)
                {
                    Assert.NotNull(mark);
                    Assert.Contains(mark.Kind, Kinds);
                    var b = mark.Bounds;
                    Assert.True(b.Width > 0 && b.Height > 0 && b.X >= 0 && b.Y >= 0 && b.X + b.Width <= width && b.Y + b.Height <= height, kit.Describe(b.ToString()));
                    Assert.True(mark.EndX >= 0 && mark.EndX <= width && mark.EndY >= 0 && mark.EndY <= height);
                    var mapped = AnnotationValidator.MapToDisplay(b, context);
                    Assert.True(double.IsFinite(mapped.X) && double.IsFinite(mapped.Y) && double.IsFinite(mapped.Width) && double.IsFinite(mapped.Height));
                }
            }
        }
    }

    private static readonly string[] Kinds = { "ring", "arrow", "label" };
}
