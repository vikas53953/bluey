# BOO-LINT-002A: no source warning-disable directives

2026-11-14. First bounded slice of Phase 1's missing no-suppress policy. Recurring source/consumer mismatches:
[#23 alias rewriting erased ratchet hits](https://github.com/vikas53953/bluey/pull/23#issuecomment-6842652780)
and [#28 compiler configuration hid diagnostics](https://github.com/vikas53953/bluey/pull/28#issuecomment-6845758415).
This rule encodes only the source-directive route, not the entire class of ways to disable diagnostics.

The CLI uses the C# compiler's Roslyn parser, from the pinned .NET SDK, to recognize real `#pragma warning disable`
syntax. It rejects all such directives in committed `src/**/*.cs` blobs, including mixed-case file extensions,
inactive branches and AnalyzerBaseline.cs. A formatting-disable pragma is conservatively rejected too. Restore,
comments, ordinary/verbatim/raw strings, nullable directives and typed suppression attributes are outside this rule.
The last two are still suppression-policy gaps, not claims of general no-suppress coverage.

[Microsoft's directive reference](https://learn.microsoft.com/dotnet/csharp/language-reference/preprocessor-directives)
and [Roslyn's own suppression implementation](https://source.dot.net/Microsoft.CodeAnalysis.CSharp.Features/CodeFixes/Suppression/CSharpSuppressionCodeFixProvider.cs.html)
are the language/parser references. No regex or matching against comments/string text decides violations.

Check input is the committed HEAD snapshot read with git, not generated files or the mutable worktree. UTF BOMs are
honored. Source symlinks and an empty source set fail closed. Errors print the rule, path/physical line and fix, never
source text. No packages are added: the console tool references SDK-shipped Roslyn assemblies. All compilation and
self-tests run on GitHub windows-latest; no local dotnet or desktop use is required.

The PR workflow runs the base revision's tool first, compiled outside the head repository's MSBuild configuration,
then the head tool/self-tests. Partial base tool pairs fail closed. The introducing target has no checker yet and
explicitly logs that rollout state. This does not guarantee execution after deletion of an optional workflow:
owner-required `no-warning-directives` branch protection is pending. Arbitrary hostile toolchain/workflow changes
remain outside this bounded source policy.

#28 retains ownership of exact typed SuppressMessage member baselines and actual shipped compiler inputs. Its current
AnalyzerBaseline files use attributes, not pragmas. #32 retains architecture-reference checks. No product code or
frozen tests/oracle change is part of the final rule. Existing src has zero warning-disable directives, so there is
no baseline or exemption list to raise. MSBuild NoWarn/WarningsNotAsErrors, severity configs, attributes, hidden
compile inputs and nullable switches remain pending in the general policy or their existing owner PRs.

Hosted red proof must contain a committed src pragma rejected by this named check. A compile failure is not proof.
The restored head must pass the real parser fixtures and source scan. See the PR body for exact SHA/run receipts.
