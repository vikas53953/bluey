using Boo.Providers;

namespace Boo.Integration.Tests;

/// <summary>
/// The reason a provider gives for an incomplete response is echoed to the user only when it is a short
/// machine word (ASCII letters, digits and underscores, 64 characters at most). Anything else falls back to
/// the generic text so provider-controlled strings never reach the notch. Kills mutation survivor #144
/// (the 64-character cap removed) and covers the rest of the guard's class: empty, whitespace, non-ASCII,
/// punctuation.
/// </summary>
public sealed class RealtimeIncompleteReasonGuardTests
{
    private const string Generic = "Realtime response incomplete.";

    private static string? IncompleteText(string reason)
    {
        var json = "{\"type\":\"response.done\",\"response\":{\"status\":\"incomplete\",\"status_details\":{\"type\":\"incomplete\",\"reason\":"
            + System.Text.Json.JsonSerializer.Serialize(reason) + "}}}";
        var parsed = RealtimeProtocol.Parse(json);
        Assert.NotNull(parsed);
        Assert.Equal("response.done", parsed!.Kind);
        return parsed.Text;
    }

    [Theory]
    [InlineData("max_output_tokens")]
    [InlineData("content_filter")]
    [InlineData("a")]
    [InlineData("Reason_2")]
    public void Short_machine_reason_is_kept(string reason)
    {
        Assert.Equal($"Realtime response incomplete ({reason}).", IncompleteText(reason));
    }

    [Theory]
    [InlineData(63)]
    [InlineData(64)]
    public void Reason_at_or_under_64_characters_is_kept(int length)
    {
        var reason = new string('a', length);
        Assert.Equal($"Realtime response incomplete ({reason}).", IncompleteText(reason));
    }

    [Theory]
    [InlineData(65)]
    [InlineData(66)]
    [InlineData(200)]
    [InlineData(4096)]
    public void Reason_over_64_characters_falls_back_to_generic_text(int length)
    {
        var reason = new string('a', length);
        var text = IncompleteText(reason);
        Assert.Equal(Generic, text);
        Assert.DoesNotContain(reason, text!, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_reason_built_from_a_valid_word_is_still_refused()
    {
        // 65 characters made only of letters, digits and underscores: only the length cap can refuse it.
        var reason = "max_output_tokens_" + new string('9', 47);
        Assert.Equal(65, reason.Length);
        Assert.Equal(Generic, IncompleteText(reason));
        Assert.Equal($"Realtime response incomplete ({reason[..64]}).", IncompleteText(reason[..64]));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\n")]
    [InlineData("max output tokens")]
    [InlineData(" max_output_tokens")]
    [InlineData("max_output_tokens\n")]
    [InlineData("max-output-tokens")]
    [InlineData("reason.with.dots")]
    [InlineData("café")]
    [InlineData("ｍａｘ")]
    [InlineData("причина")]
    [InlineData("١٢٣")]
    [InlineData("<b>x</b>")]
    public void Reason_outside_the_machine_word_class_falls_back_to_generic_text(string reason)
    {
        Assert.Equal(Generic, IncompleteText(reason));
    }

    [Fact]
    public void Non_ascii_reason_over_64_characters_falls_back_to_generic_text()
    {
        Assert.Equal(Generic, IncompleteText(new string('é', 65)));
    }
}
