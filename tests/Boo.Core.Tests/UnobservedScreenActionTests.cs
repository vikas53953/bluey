using Boo.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Boo.Core.Tests;

/// <summary>
/// Review regression (PR #3 at f3039ff, 13 Nov 2026): when the screen capture for an explicit task failed, the
/// request reached the model with no frame and no control observation, and a model-proposed click or type_text
/// still went to the approval preview with a fallback target. The app now routes every explicit-task reply through
/// <see cref="ActionPolicy.WithoutUnobservedScreenActions"/> before choosing an action. These cases pin that guard:
/// without an observation both screen-action types vanish, non-screen actions keep their order, and a present
/// observation changes nothing.
/// </summary>
public sealed class UnobservedScreenActionTests
{
    private const long Generation = 11;

    public static IEnumerable<object[]> ScreenActions()
    {
        yield return new object[] { "click", "pixel", new[] { ("x", "14"), ("y", "17") } };
        yield return new object[] { "click", "by-id", new[] { ("automationId", "save-button"), ("name", "Save") } };
        yield return new object[] { "type_text", "editor", new[] { ("automationId", "edit-field"), ("text", "hello") } };
        yield return new object[] { "type_text", "editor-by-name", new[] { ("name", "Editor"), ("text", "hello") } };
    }

    [Theory]
    [MemberData(nameof(ScreenActions))]
    public void Screen_action_without_an_observation_is_dropped(string name, string target, (string Key, string Value)[] arguments)
    {
        var actions = new[] { Request(name, target, arguments) };

        var offered = ActionPolicy.WithoutUnobservedScreenActions(actions, null);

        Assert.Empty(offered);
        Assert.True(ActionPolicy.RequiresScreenObservation(name));
    }

    [Fact]
    public void Mixed_reply_without_an_observation_keeps_only_non_screen_actions_in_order()
    {
        var actions = new[]
        {
            Request("click", "pixel", ("x", "14"), ("y", "17")),
            Request("write_file", "notes", ("path", "notes.txt"), ("content", "hello")),
            Request("type_text", "editor", ("automationId", "edit-field"), ("text", "hello")),
            Request("search", "docs", ("text", "needle")),
            Request("open_url", "docs", ("url", "https://example.test/")),
            Request("click", "by-id", ("automationId", "save-button")),
            Request("open_app", "notepad", ("name", "notepad")),
            Request("delete_file", "old", ("path", "old.txt")),
            Request("inspect", "window")
        };

        var offered = ActionPolicy.WithoutUnobservedScreenActions(actions, null);

        Assert.Equal(
            new[] { "write_file", "search", "open_url", "open_app", "delete_file", "inspect" },
            offered.Select(action => action.Name));
        Assert.All(offered, action => Assert.Equal(Generation, action.Generation));
    }

    [Fact]
    public void Every_catalog_action_other_than_click_and_type_text_survives_without_an_observation()
    {
        var actions = ActionPolicy.ToolNames.Select(name => Request(name, name)).ToArray();

        var offered = ActionPolicy.WithoutUnobservedScreenActions(actions, null);

        Assert.Equal(
            ActionPolicy.ToolNames.Where(name => name is not ("click" or "type_text")).OrderBy(name => name, StringComparer.Ordinal),
            offered.Select(action => action.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.DoesNotContain(offered, action => action.Name is "click" or "type_text");
    }

    [Fact]
    public void Present_observation_keeps_every_action_including_screen_actions()
    {
        var context = new ScreenContext(
            Guid.NewGuid(), Generation,
            new DisplayInfo("display-1", "Fixture", new RectD(0, 0, 1920, 1080), 1),
            1920, 1080,
            new WindowIdentity(11, 22, "fixture", new RectD(0, 0, 800, 600)),
            DateTimeOffset.UtcNow);
        var controls = ControlObservation.Create(context, Array.Empty<ControlCandidate>());
        var actions = new[]
        {
            Request("click", "pixel", ("x", "14"), ("y", "17")),
            Request("type_text", "editor", ("automationId", "edit-field"), ("text", "hello")),
            Request("write_file", "notes", ("path", "notes.txt"), ("content", "hello"))
        };

        var offered = ActionPolicy.WithoutUnobservedScreenActions(actions, controls);

        Assert.Same(actions, offered);
    }

    [Fact]
    public void Empty_reply_without_an_observation_stays_empty()
    {
        var offered = ActionPolicy.WithoutUnobservedScreenActions(Array.Empty<ToolRequest>(), null);

        Assert.Empty(offered);
    }

    private static ToolRequest Request(string name, string target, params (string Key, string Value)[] arguments)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in arguments)
        {
            map[key] = value;
        }
        return new ToolRequest(name, target, map, Generation);
    }
}
