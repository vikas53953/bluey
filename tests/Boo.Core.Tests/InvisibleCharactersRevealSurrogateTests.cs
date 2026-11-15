using Boo.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Boo.Core.Tests;

/// <summary>
/// Issue #145 (mutation survivor): removing the visible [U+XXXX] marker for half of a broken surrogate pair in
/// <c>InvisibleCharacters.Reveal</c> passed every gate, so an approval card could display a raw, unrenderable
/// code unit instead of telling the reader something is hidden there. These tests pin the whole class: a lone
/// high surrogate, a lone low surrogate, a reversed pair and doubled halves, at the start, middle and end, both in
/// Reveal itself and on the approval card (ActionPreviewField.DisplayValue, via ActionPreviewBuilder). Valid
/// surrogate pairs (emoji) must stay intact.
/// Lone surrogates are written as &lt;XXXX&gt; tokens and built at run time from code points, because test-data
/// strings holding lone surrogates can be mangled on the way into a test (seen in #139).
/// </summary>
public sealed class InvisibleCharactersRevealSurrogateTests
{
    private const long Generation = 145;
    private const int BooPid = 4545;

    private static readonly WindowIdentity Notepad = new(100, 900, "notepad", new RectD(0, 0, 800, 600));

    // input template, expected display. <XXXX> is one UTF-16 code unit.
    public static IEnumerable<object[]> BrokenSurrogates() => new[]
    {
        new object[] { "<D800>ab", "[U+D800]ab" },
        new object[] { "a<D800>b", "a[U+D800]b" },
        new object[] { "ab<D800>", "ab[U+D800]" },
        new object[] { "<D800>", "[U+D800]" },
        new object[] { "<DC00>ab", "[U+DC00]ab" },
        new object[] { "a<DC00>b", "a[U+DC00]b" },
        new object[] { "ab<DC00>", "ab[U+DC00]" },
        new object[] { "<DFFF>", "[U+DFFF]" },
        new object[] { "a<DBFF>b", "a[U+DBFF]b" },
        new object[] { "<DE00><D83D>", "[U+DE00][U+D83D]" },
        new object[] { "a<DE00><D83D>b", "a[U+DE00][U+D83D]b" },
        new object[] { "a<D83D><D83D>b", "a[U+D83D][U+D83D]b" },
        new object[] { "a<DE00><DE00>b", "a[U+DE00][U+DE00]b" },
        new object[] { "<D83D><D83D><DE00>", "[U+D83D]<D83D><DE00>" },
        new object[] { "<D83D><DE00><DE00>", "<D83D><DE00>[U+DE00]" },
        new object[] { "<D83D><DE00>x<D800>y<D83D><DE00>", "<D83D><DE00>x[U+D800]y<D83D><DE00>" },
    };

    // Valid pairs across the supplementary planes; Reveal must return them unchanged.
    public static IEnumerable<object[]> ValidPairs() => new[]
    {
        new object[] { "a<D83D><DE00>b" },                 // U+1F600 grinning face
        new object[] { "<D83D><DC4D><D83C><DF89>" },       // U+1F44D U+1F389
        new object[] { "<D800><DC00>" },                    // U+10000, lowest supplementary code point
        new object[] { "x<DBFF><DFFD>" },                   // U+10FFFD, private use, not Format
        new object[] { "<D83D><DC4B><D83C><DFFD>" },       // waving hand + skin tone
        new object[] { "<D83C><DDEE><D83C><DDF3>" },       // flag (regional indicators)
    };

    [Theory]
    [MemberData(nameof(BrokenSurrogates))]
    public void Reveal_shows_each_broken_surrogate_as_an_exact_marker(string template, string expectedTemplate)
    {
        var shown = InvisibleCharacters.Reveal(Build(template));

        AssertShown(Build(expectedTemplate), shown, template);
    }

    [Theory]
    [MemberData(nameof(BrokenSurrogates))]
    public void Approval_card_field_shows_each_broken_surrogate_as_an_exact_marker(string template, string expectedTemplate)
    {
        var field = new ActionPreviewField("text", "Text", Build(template));

        AssertShown(Build(expectedTemplate), field.DisplayValue, template);
    }

    [Theory]
    [MemberData(nameof(BrokenSurrogates))]
    public void Type_text_card_shows_each_broken_surrogate_as_an_exact_marker(string template, string expectedTemplate)
    {
        var preview = ActionPreviewBuilder.Create(TypeText(Build(template)), Authority(), Notepad, BooPid);
        var shown = preview.Fields.Single(field => field.Key == "text").DisplayValue;

        AssertShown(Build(expectedTemplate), shown, template);
        Assert.False(preview.CanApprove, $"{template} must not be approvable.");
    }

    // The class check: every lone surrogate code unit, high or low, at the start, middle and end.
    [Fact]
    public void Every_lone_surrogate_code_unit_is_shown_as_its_marker_in_every_position()
    {
        var failures = new List<string>();
        for (var codeUnit = 0xD800; codeUnit <= 0xDFFF; codeUnit++)
        {
            var lone = ((char)codeUnit).ToString();
            var marker = "[U+" + codeUnit.ToString("X4", CultureInfo.InvariantCulture) + "]";
            foreach (var (input, expected) in new[]
            {
                (lone + "ab", marker + "ab"),
                ("a" + lone + "b", "a" + marker + "b"),
                ("ab" + lone, "ab" + marker),
            })
            {
                var shown = InvisibleCharacters.Reveal(input);
                var card = new ActionPreviewField("text", "Text", input).DisplayValue;
                if (!string.Equals(shown, expected, StringComparison.Ordinal) || !string.Equals(card, expected, StringComparison.Ordinal))
                {
                    failures.Add($"{Escape(input)} -> reveal '{Escape(shown)}', card '{Escape(card)}', expected '{expected}'");
                }
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} broken surrogate displays wrong, first: {failures.FirstOrDefault()}");
    }

    [Theory]
    [MemberData(nameof(ValidPairs))]
    public void Valid_surrogate_pairs_are_kept_intact(string template)
    {
        var text = Build(template);

        Assert.Equal(text, InvisibleCharacters.Reveal(text));
        Assert.Equal(text, new ActionPreviewField("text", "Text", text).DisplayValue);
    }

    private static void AssertShown(string expected, string shown, string template)
    {
        Assert.True(string.Equals(expected, shown, StringComparison.Ordinal),
            $"{template}: shown '{Escape(shown)}', expected '{Escape(expected)}'.");
        Assert.True(IsWellFormed(shown), $"{template}: the display still holds a raw broken surrogate: '{Escape(shown)}'.");
    }

    private static bool IsWellFormed(string value)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != System.Buffers.OperationStatus.Done)
            {
                return false;
            }

            remaining = remaining[consumed..];
        }

        return true;
    }

    private static string Build(string template) =>
        Regex.Replace(template, "<([0-9A-F]{4})>",
            match => ((char)int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());

    private static ToolRequest TypeText(string text) =>
        new("type_text", "notepad", new Dictionary<string, string>(StringComparer.Ordinal) { ["text"] = text }, Generation);

    private static TaskAuthority Authority() =>
        new(Generation, ExplicitUserTask: true, new HashSet<string>(new[] { "type_text" }, StringComparer.Ordinal));

    private static string Escape(string value) =>
        string.Concat(value.Select(character => character < 0x20 || character > 0x7E
            ? "\\u" + ((int)character).ToString("X4", CultureInfo.InvariantCulture)
            : character.ToString()));
}
