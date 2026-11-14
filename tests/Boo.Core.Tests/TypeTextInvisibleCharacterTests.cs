using Boo.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Boo.Core.Tests;

/// <summary>
/// Issue #127: the type_text guard checked control characters plus a hand-written list of zero-width and bidi
/// characters, so other invisible Unicode Format (Cf) characters such as U+061C ARABIC LETTER MARK, U+200E and
/// U+200F passed: "pay‎pal" reads as "paypal" on the approval card but types an extra hidden character.
/// The class fix is one policy shared with open_url: every Format character (in any plane), every control
/// character except tab / line feed / carriage return, and every unpaired surrogate is refused, on the approval
/// card (preview) and again by the executor backstop (the same WindowActionGuard.BlockedReason). Visible text in
/// any script, emoji, tabs and line breaks still pass. The approval card also shows any hidden character as a
/// visible [U+XXXX] marker, so even a refused card never displays different text from the request.
/// </summary>
public sealed class TypeTextInvisibleCharacterTests
{
    private const long Generation = 127;
    private const int BooPid = 4242;

    private static readonly WindowIdentity Notepad = new(100, 900, "notepad", new RectD(0, 0, 800, 600));

    // The three characters the issue names, at the start, middle and end of otherwise visible text.
    public static IEnumerable<object[]> IssueCharacters()
    {
        foreach (var codePoint in new[] { 0x061C, 0x200E, 0x200F })
        {
            var hidden = char.ConvertFromUtf32(codePoint);
            yield return new object[] { codePoint, "middle", "pay" + hidden + "pal" };
            yield return new object[] { codePoint, "start", hidden + "paypal" };
            yield return new object[] { codePoint, "end", "paypal" + hidden };
        }
    }

    // Every range the old hand-written list blocked, plus control characters (C0, DEL, C1) and unpaired surrogates.
    public static IEnumerable<object[]> PreviouslyBlockedAndControlCharacters()
    {
        var codePoints = new List<int> { 0x200B, 0x200C, 0x200D, 0x2060, 0xFEFF };
        codePoints.AddRange(Enumerable.Range(0x202A, 5)); // LRE, RLE, PDF, LRO, RLO
        codePoints.AddRange(Enumerable.Range(0x2066, 4)); // LRI, RLI, FSI, PDI
        codePoints.AddRange(Enumerable.Range(0x0000, 0x20).Where(c => c is not ('\t' or '\n' or '\r')));
        codePoints.Add(0x007F);
        codePoints.AddRange(Enumerable.Range(0x0080, 0x20));
        foreach (var codePoint in codePoints)
        {
            yield return new object[] { "U+" + codePoint.ToString("X4", CultureInfo.InvariantCulture), "a" + (char)codePoint + "b" };
        }

        yield return new object[] { "lone high surrogate U+D800", "a\uD800b" };
        yield return new object[] { "lone low surrogate U+DC00", "a\uDC00b" };
        yield return new object[] { "reversed pair U+DE00 U+D83D", "a\uDE00\uD83Db" };
        yield return new object[] { "trailing high surrogate", "ab\uD83D" };
    }

    // Visible multilingual text, emoji and the supported tab / line breaks must still be typed.
    public static IEnumerable<object[]> AllowedText()
    {
        yield return new object[] { "English", "Hello, world! 123 (a+b)=c" };
        yield return new object[] { "Arabic", "مرحبا بالعالم" };
        yield return new object[] { "Hebrew", "שלום עולם" };
        yield return new object[] { "Hindi", "नमस्ते दुनिया" };
        yield return new object[] { "Chinese", "你好世界" };
        yield return new object[] { "Japanese", "こんにちは世界" };
        yield return new object[] { "Korean", "안녕하세요" };
        yield return new object[] { "accents", "café naïve Straße é" };
        yield return new object[] { "emoji", "👍🎉😀" };
        yield return new object[] { "emoji with variation selector", "❤️ ✔️" };
        yield return new object[] { "emoji skin tone", "👋🏽" };
        yield return new object[] { "flag", "🇮🇳" };
        yield return new object[] { "tab and line breaks", "a\tb\r\nc\nd\re" };
        yield return new object[] { "no-break space", "10 kg" };
    }

    [Theory]
    [MemberData(nameof(IssueCharacters))]
    public void Issue_127_direction_marks_are_refused_by_the_guard(int codePoint, string position, string text)
    {
        var reason = WindowActionGuard.BlockedReason(TypeText(text), Notepad, BooPid);

        Assert.True(reason is not null, $"U+{codePoint:X4} at the {position} must be refused before approval.");
        Assert.Contains("hidden control characters", reason);
    }

    [Theory]
    [MemberData(nameof(IssueCharacters))]
    public void Issue_127_direction_marks_cannot_be_approved_on_the_card(int codePoint, string position, string text)
    {
        var preview = ActionPreviewBuilder.Create(TypeText(text), Authority(), Notepad, BooPid);

        Assert.False(preview.CanApprove, $"U+{codePoint:X4} at the {position} must not be approvable.");
        Assert.NotNull(preview.BlockedReason);
    }

    [Theory]
    [MemberData(nameof(IssueCharacters))]
    public void Issue_127_card_shows_the_hidden_character_visibly(int codePoint, string position, string text)
    {
        var preview = ActionPreviewBuilder.Create(TypeText(text), Authority(), Notepad, BooPid);
        var shown = preview.Fields.Single(field => field.Key == "text").DisplayValue;

        var marker = "[U+" + codePoint.ToString("X4", CultureInfo.InvariantCulture) + "]";
        Assert.True(shown.Contains(marker, StringComparison.Ordinal), $"{position}: card shows '{Escape(shown)}', expected {marker}.");
        Assert.True(shown.All(character => char.GetUnicodeCategory(character) != UnicodeCategory.Format),
            $"{position}: the card still displays an invisible character: '{Escape(shown)}'.");
    }

    [Theory]
    [MemberData(nameof(PreviouslyBlockedAndControlCharacters))]
    public void Previously_blocked_and_control_characters_stay_refused(string name, string text)
    {
        Assert.True(WindowActionGuard.BlockedReason(TypeText(text), Notepad, BooPid) is not null, $"{name} must be refused.");
        Assert.False(ActionPreviewBuilder.Create(TypeText(text), Authority(), Notepad, BooPid).CanApprove, $"{name} must not be approvable.");
    }

    // The class check: no Unicode Format character in the Basic Multilingual Plane slips through.
    [Fact]
    public void Every_format_character_in_the_basic_plane_is_refused()
    {
        var formatCharacters = Enumerable.Range(0, 0x10000)
            .Where(codePoint => codePoint is < 0xD800 or > 0xDFFF)
            .Where(codePoint => CharUnicodeInfo.GetUnicodeCategory((char)codePoint) == UnicodeCategory.Format)
            .ToList();
        Assert.True(formatCharacters.Count >= 40, $"sanity: expected about 40+ BMP format characters, found {formatCharacters.Count}.");

        var missed = formatCharacters
            .Where(codePoint => WindowActionGuard.BlockedReason(TypeText("a" + (char)codePoint + "b"), Notepad, BooPid) is null)
            .Select(codePoint => "U+" + codePoint.ToString("X4", CultureInfo.InvariantCulture))
            .ToList();

        Assert.True(missed.Count == 0, $"{missed.Count} format characters pass the type_text guard: {string.Join(", ", missed)}");
    }

    // Format characters outside the BMP (tag characters used to smuggle hidden text, musical and shorthand
    // format controls) arrive as surrogate pairs, so a per-char check alone cannot see them.
    [Fact]
    public void Every_format_character_outside_the_basic_plane_is_refused()
    {
        var formatRunes = Enumerable.Range(0x10000, 0x110000 - 0x10000)
            .Select(codePoint => new Rune(codePoint))
            .Where(rune => Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)
            .ToList();
        Assert.Contains(formatRunes, rune => rune.Value == 0xE0041); // TAG LATIN CAPITAL LETTER A

        var missed = formatRunes
            .Where(rune => WindowActionGuard.BlockedReason(TypeText("a" + rune + "b"), Notepad, BooPid) is null)
            .Select(rune => "U+" + rune.Value.ToString("X5", CultureInfo.InvariantCulture))
            .ToList();

        Assert.True(missed.Count == 0, $"{missed.Count} supplementary format characters pass the type_text guard: {string.Join(", ", missed)}");
    }

    // Every BMP control character except the three supported ones is refused.
    [Fact]
    public void Every_control_character_except_tab_and_line_breaks_is_refused()
    {
        var missed = Enumerable.Range(0, 0x10000)
            .Where(codePoint => char.IsControl((char)codePoint) && codePoint is not ('\t' or '\n' or '\r'))
            .Where(codePoint => WindowActionGuard.BlockedReason(TypeText("a" + (char)codePoint + "b"), Notepad, BooPid) is null)
            .Select(codePoint => "U+" + codePoint.ToString("X4", CultureInfo.InvariantCulture))
            .ToList();

        Assert.True(missed.Count == 0, $"control characters pass the type_text guard: {string.Join(", ", missed)}");
    }

    [Theory]
    [MemberData(nameof(AllowedText))]
    public void Visible_multilingual_text_emoji_and_line_breaks_are_allowed(string name, string text)
    {
        Assert.True(WindowActionGuard.BlockedReason(TypeText(text), Notepad, BooPid) is null, $"{name} must be allowed.");

        var preview = ActionPreviewBuilder.Create(TypeText(text), Authority(), Notepad, BooPid);
        Assert.True(preview.CanApprove, $"{name} must be approvable; blocked: {preview.BlockedReason}");
        Assert.Equal(text, preview.Fields.Single(field => field.Key == "text").DisplayValue);
    }

    // Decision (issue #127): U+200D ZERO WIDTH JOINER is an invisible Format character, so emoji sequences built
    // with it (family, profession and some flag emoji) are refused. Approval-text fidelity is the safety property;
    // the same emoji without joiners still types (see the allowed cases).
    [Theory]
    [InlineData("👨‍👩‍👧")]
    [InlineData("👩‍💻")]
    [InlineData("🏳️‍🌈")]
    public void Emoji_joined_with_zero_width_joiner_are_refused(string text)
    {
        Assert.NotNull(WindowActionGuard.BlockedReason(TypeText(text), Notepad, BooPid));
        Assert.False(ActionPreviewBuilder.Create(TypeText(text), Authority(), Notepad, BooPid).CanApprove);
    }

    private static ToolRequest TypeText(string text) =>
        new("type_text", "notepad", new Dictionary<string, string>(StringComparer.Ordinal) { ["text"] = text }, Generation);

    private static TaskAuthority Authority() =>
        new(Generation, ExplicitUserTask: true, new HashSet<string>(new[] { "type_text" }, StringComparer.Ordinal));

    private static string Escape(string value) =>
        string.Concat(value.Select(character => character < 0x20 || character > 0x7E
            ? "\\u" + ((int)character).ToString("X4", CultureInfo.InvariantCulture)
            : character.ToString()));
}
