using Boo.Core;
using Xunit;

namespace Boo.Core.Tests;

/// <summary>
/// Regression: the notch caption window ("…" + last 160 characters) slides as a spoken reply streams in, so two
/// consecutive windows share no prefix. Continuity must be judged on the full text or the typewriter restarts on
/// every delta past 160 characters.
/// </summary>
public class CaptionStreamTests
{
    private static string Words(int count, string word = "word")
        => string.Join(' ', Enumerable.Range(1, count).Select(i => word + i));

    [Fact]
    public void Growing_text_continues()
    {
        Assert.True(CaptionStream.Continues("Rule 1043 allows", "Rule 1043 allows any source"));
    }

    [Fact]
    public void Same_text_continues()
    {
        Assert.True(CaptionStream.Continues("Rule 1043", "Rule 1043"));
    }

    [Fact]
    public void Different_text_restarts()
    {
        Assert.False(CaptionStream.Continues("Rule 1043 allows", "Still on the Q4 rule review?"));
    }

    [Fact]
    public void Shorter_text_restarts()
    {
        Assert.False(CaptionStream.Continues("Rule 1043 allows any source", "Rule 1043"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_before_is_a_new_caption(string? previous)
    {
        Assert.False(CaptionStream.Continues(previous, "Rule 1043"));
    }

    [Fact]
    public void Whitespace_changes_do_not_restart()
    {
        Assert.True(CaptionStream.Continues("Rule 1043\nallows", "Rule   1043 allows  any\r\nsource"));
        Assert.Equal("Rule 1043 allows any source", CaptionStream.Normalize("Rule   1043 allows  any\r\nsource "));
        Assert.Equal(string.Empty, CaptionStream.Normalize(" \n\t"));
    }

    [Fact]
    public void Long_streamed_reply_keeps_continuing_past_the_display_window()
    {
        // 60 words ≈ 420 characters: well past the 160-character window the notch draws.
        var previous = Words(60);
        var next = previous + " and one more";
        Assert.True(previous.Length > 160);
        Assert.True(CaptionStream.Continues(previous, next));

        // The displayed windows of the same two texts share no prefix: judging on them would restart typing.
        static string Window(string text) => "…" + text[^160..];
        Assert.False(Window(next).StartsWith(Window(previous), StringComparison.Ordinal));
    }

    [Fact]
    public void Every_delta_of_a_streamed_reply_continues()
    {
        var full = Words(80);
        var typed = string.Empty;
        foreach (var token in full.Split(' '))
        {
            var next = typed.Length == 0 ? token : typed + " " + token;
            if (typed.Length > 0)
            {
                Assert.True(CaptionStream.Continues(typed, next), $"delta at {next.Length} characters should continue");
            }
            typed = next;
        }
    }
}
