using Boo.Core;
using System;
using System.Collections.Generic;

namespace Boo.Core.Tests;

/// <summary>
/// Mutation review (13 Nov 2026, PR #3 "CODEX MUTATION 2/3"): weakening the open_url character guard from
/// "control, whitespace, surrogate or format character" to "control character only" survived every test. These
/// cases kill that mutant through the public approval path: an otherwise valid absolute HTTP(S) URL that carries
/// one Unicode whitespace or invisible format character anywhere is denied, and the same URL without it is allowed.
/// </summary>
public sealed class OpenUrlInvisibleCharacterTests
{
    private const long Generation = 11;

    // U+00A0 no-break space, U+200B zero width space, U+2028 line separator, U+FEFF zero width no-break space.
    public static IEnumerable<object[]> InvisibleCharacters()
    {
        foreach (var character in new[] { (char)0x00A0, (char)0x200B, (char)0x2028, (char)0xFEFF })
        {
            yield return new object[] { character, "leading", character + "https://example.test/path?q=1" };
            yield return new object[] { character, "trailing", "https://example.test/path?q=1" + character };
            yield return new object[] { character, "host", "https://exam" + character + "ple.test/path?q=1" };
            yield return new object[] { character, "path", "https://example.test/pa" + character + "th?q=1" };
            yield return new object[] { character, "query", "https://example.test/path?q=" + character + "1" };
        }
    }

    [Theory]
    [MemberData(nameof(InvisibleCharacters))]
    public void Open_url_with_an_invisible_character_is_denied(char character, string position, string url)
    {
        var request = Request("open_url", url);
        var authority = Authority("open_url");

        var decision = ActionPolicy.Evaluate(request, authority, ActionPolicy.Approve(request));

        Assert.True(decision.Verdict == PermissionVerdict.Denied,
            $"U+{(int)character:X4} at the {position} must be denied; verdict was {decision.Verdict} ({decision.Reason}).");
    }

    [Theory]
    [InlineData("https://example.test/path?q=1")]
    [InlineData("http://example.test/")]
    [InlineData("https://example.test/a%20b?q=x%20y")]
    public void Clean_absolute_http_url_is_allowed(string url)
    {
        var request = Request("open_url", url);
        var authority = Authority("open_url");

        var decision = ActionPolicy.Evaluate(request, authority, ActionPolicy.Approve(request));

        Assert.Equal(PermissionVerdict.Allowed, decision.Verdict);
    }

    [Theory]
    [MemberData(nameof(InvisibleCharacters))]
    public void Invisible_character_is_unsafe_even_where_Uri_accepts_it(char character, string position, string url)
    {
        Assert.True(ActionPolicy.IsUnsafeUrlCharacter(character), $"U+{(int)character:X4} ({position}) must be unsafe.");
        Assert.False(ActionPolicy.IsSafeHttpUrl(url));
    }

    private static ToolRequest Request(string name, string target) =>
        new(name, target, new Dictionary<string, string>(StringComparer.Ordinal), Generation);

    private static TaskAuthority Authority(string tool) =>
        new(Generation, ExplicitUserTask: true, new HashSet<string>(new[] { tool }, StringComparer.Ordinal));
}
