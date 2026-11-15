using Boo.Core;

namespace Boo.Core.Tests;

/// <summary>
/// A voice task's function call must be completed exactly once as soon as its reply is rendered, unless an action
/// is really offered for approval. Deciding on the proposed list instead of the offered action left a reply whose
/// only actions were filtered out (click/type_text with no screen observation) waiting for an approval that no
/// control could give, so the realtime conversation never got a function result.
/// </summary>
public sealed class RealtimeToolResultTests
{
    private const string ReplyText = "I would click the Save button.";

    private static ToolRequest Request(string name, string target = "x", params (string Key, string Value)[] arguments) =>
        new(name, target, arguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), Generation: 1);

    private static ToolRequest PixelClick() => Request("click", "pixel", ("x", "120"), ("y", "80"));
    private static ToolRequest IdType() => Request("type_text", "box", ("automationId", "QaBox"), ("text", "hello"));
    private static ToolRequest WriteFile() => Request("write_file", "notes.txt", ("path", "notes.txt"), ("contents", "hi"));

    [Fact]
    public void Offered_action_defers_the_result_until_approval()
    {
        var offered = WriteFile();

        Assert.Null(ActionPolicy.RealtimeToolResult(offered, new[] { PixelClick(), offered }, ReplyText, explicitTask: true));
    }

    [Theory]
    [InlineData("click")]
    [InlineData("type_text")]
    public void Task_whose_only_action_was_filtered_out_completes_with_the_refusal(string name)
    {
        var proposed = new[] { name == "click" ? PixelClick() : IdType() };

        var result = ActionPolicy.RealtimeToolResult(offeredAction: null, proposed, ReplyText, explicitTask: true);

        Assert.Equal(ActionPolicy.UnobservedScreenActionRefusal, result);
        Assert.NotNull(result);
    }

    [Fact]
    public void Task_with_several_filtered_out_actions_completes_with_the_refusal_once()
    {
        var result = ActionPolicy.RealtimeToolResult(null, new[] { PixelClick(), IdType() }, ReplyText, explicitTask: true);

        Assert.Equal(ActionPolicy.UnobservedScreenActionRefusal, result);
    }

    [Fact]
    public void Refusal_names_the_missing_observation_and_forbids_acting()
    {
        Assert.Contains("no screen observation", ActionPolicy.UnobservedScreenActionRefusal, StringComparison.Ordinal);
        Assert.Contains("nothing was offered", ActionPolicy.UnobservedScreenActionRefusal, StringComparison.Ordinal);
        Assert.Contains("without taking an action", ActionPolicy.UnobservedScreenActionRefusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Task_reply_without_actions_completes_with_the_reply_text()
    {
        Assert.Equal(ReplyText, ActionPolicy.RealtimeToolResult(null, Array.Empty<ToolRequest>(), ReplyText, explicitTask: true));
    }

    [Fact]
    public void Non_task_reply_completes_with_the_reply_text_even_when_actions_were_proposed()
    {
        Assert.Equal(ReplyText, ActionPolicy.RealtimeToolResult(null, new[] { PixelClick(), WriteFile() }, ReplyText, explicitTask: false));
    }

    [Fact]
    public void No_observation_voice_task_with_a_click_only_reply_completes_immediately_with_the_refusal()
    {
        var proposed = new[] { PixelClick() };

        var offered = ActionPolicy.WithoutUnobservedScreenActions(proposed, controls: null).FirstOrDefault();
        var result = ActionPolicy.RealtimeToolResult(offered, proposed, ReplyText, explicitTask: true);

        Assert.Null(offered);
        Assert.Equal(ActionPolicy.UnobservedScreenActionRefusal, result);
    }

    [Fact]
    public void No_observation_voice_task_with_a_mixed_reply_still_defers_for_the_surviving_action()
    {
        var proposed = new[] { PixelClick(), WriteFile() };

        var offered = ActionPolicy.WithoutUnobservedScreenActions(proposed, controls: null).FirstOrDefault();

        Assert.Equal("write_file", offered?.Name);
        Assert.Null(ActionPolicy.RealtimeToolResult(offered, proposed, ReplyText, explicitTask: true));
    }

    [Fact]
    public void Observed_click_is_offered_and_defers_the_result()
    {
        var proposed = new[] { PixelClick() };
        var context = new ScreenContext(
            Guid.NewGuid(), 1,
            new DisplayInfo("display-1", "Fixture", new RectD(0, 0, 1920, 1080), 1),
            1920, 1080,
            new WindowIdentity(11, 22, "fixture", new RectD(0, 0, 800, 600)),
            DateTimeOffset.UtcNow);
        var controls = ControlObservation.Create(context, Array.Empty<ControlCandidate>());

        var offered = ActionPolicy.WithoutUnobservedScreenActions(proposed, controls).FirstOrDefault();

        Assert.NotNull(offered);
        Assert.Null(ActionPolicy.RealtimeToolResult(offered, proposed, ReplyText, explicitTask: true));
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => ActionPolicy.RealtimeToolResult(null, null!, ReplyText, true));
        Assert.Throws<ArgumentNullException>(() => ActionPolicy.RealtimeToolResult(null, Array.Empty<ToolRequest>(), null!, true));
    }
}
