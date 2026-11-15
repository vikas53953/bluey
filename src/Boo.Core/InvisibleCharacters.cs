using System.Buffers;
using System.Globalization;
using System.Text;

namespace Boo.Core;

/// <summary>
/// The one policy for characters that can make an approval card show different text from what Boo acts on
/// (issue #127). A character is hidden when it is a control character, any Unicode Format (Cf) character in
/// any plane (zero-width, bidi marks and overrides, U+061C, soft hyphen, tag characters ...), or half of a
/// broken surrogate pair. The open_url check, the type_text guard (approval card and executor backstop) and
/// the card's display all use this list, so a new invisible character is handled everywhere at once.
/// U+200D ZERO WIDTH JOINER is Format too, so joined emoji sequences are refused: fidelity first.
/// </summary>
internal static class InvisibleCharacters
{
    /// <summary>Per-char check for callers that reject every surrogate anyway (URLs).</summary>
    public static bool IsHidden(char character) =>
        char.IsControl(character) || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format;

    public static bool IsHidden(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format;

    /// <summary>
    /// True when the text holds a hidden character. Whole code points are checked, so Format characters
    /// outside the Basic Multilingual Plane are seen; a broken surrogate pair always counts as hidden.
    /// Tab, line feed and carriage return are allowed only when <paramref name="allowTabsAndLineBreaks"/>.
    /// </summary>
    public static bool Contains(string text, bool allowTabsAndLineBreaks)
    {
        var remaining = text.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done)
            {
                return true;
            }

            remaining = remaining[consumed..];
            if (allowTabsAndLineBreaks && rune.Value is '\t' or '\n' or '\r')
            {
                continue;
            }

            if (IsHidden(rune))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The text with every hidden character (except tab and line breaks) replaced by a visible [U+XXXX]
    /// marker, so an approval card never displays a character the reader cannot see.
    /// </summary>
    public static string Reveal(string text)
    {
        if (!Contains(text, allowTabsAndLineBreaks: true))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 16);
        var remaining = text.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done)
            {
                builder.Append(remaining[0]);
                remaining = remaining[1..];
                continue;
            }

            remaining = remaining[consumed..];
            if (rune.Value is not ('\t' or '\n' or '\r') && IsHidden(rune))
            {
                builder.Append("[U+").Append(rune.Value.ToString("X4", CultureInfo.InvariantCulture)).Append(']');
            }
            else
            {
                builder.Append(rune.ToString());
            }
        }

        return builder.ToString();
    }
}
