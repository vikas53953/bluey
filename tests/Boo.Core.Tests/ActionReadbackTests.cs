using System.Linq;
using Boo.Core;

namespace Boo.Core.Tests;

public sealed class ActionReadbackTests
{
    private static readonly WindowIdentity Fixture = new(4242, 77, "Boo.QaWorkspace", new RectD(0, 0, 1000, 800));

    // ---------- ControlDiff.Compare ----------

    [Fact]
    public void Compare_rejects_null_arguments()
    {
        var observation = Observe(Fixture, Field("a", "x"));

        Assert.Throws<ArgumentNullException>(() => ControlDiff.Compare(null!, observation));
        Assert.Throws<ArgumentNullException>(() => ControlDiff.Compare(observation, null!));
    }

    [Fact]
    public void Compare_rejects_observations_of_a_different_window_handle()
    {
        var before = Observe(Fixture, Field("a", "x"));
        var after = Observe(Fixture with { Handle = 9999 }, Field("a", "y"));

        Assert.Throws<ArgumentException>(() => ControlDiff.Compare(before, after));
    }

    [Fact]
    public void Compare_rejects_observations_of_a_different_process()
    {
        var before = Observe(Fixture, Field("a", "x"));
        var after = Observe(Fixture with { ProcessId = 78 }, Field("a", "y"));

        Assert.Throws<ArgumentException>(() => ControlDiff.Compare(before, after));
    }

    [Fact]
    public void Compare_accepts_the_same_window_after_it_moved_or_resized()
    {
        var before = Observe(Fixture, Field("a", "x"));
        var after = Observe(Fixture with { Bounds = new RectD(5, 5, 900, 700) }, Field("a", "y"));

        var change = Assert.Single(ControlDiff.Compare(before, after));
        Assert.Equal(new ControlChange("a", "Value", "x", "y"), change);
    }

    [Fact]
    public void Compare_returns_nothing_when_nothing_changed()
    {
        var before = Observe(Fixture, Field("a", "x"), Button("b", "Go"));
        var after = Observe(Fixture, Field("a", "x"), Button("b", "Go"));

        Assert.Empty(ControlDiff.Compare(before, after));
    }

    [Fact]
    public void Compare_reports_name_before_value_for_the_same_control()
    {
        var before = Observe(Fixture, Control("c", "Old name", "old value"));
        var after = Observe(Fixture, Control("c", "New name", "new value"));

        Assert.Equal(new[]
        {
            new ControlChange("c", "Name", "Old name", "New name"),
            new ControlChange("c", "Value", "old value", "new value")
        }, ControlDiff.Compare(before, after));
    }

    [Fact]
    public void Compare_follows_the_order_of_the_before_observation_and_ignores_one_sided_controls()
    {
        var before = Observe(Fixture,
            Control("first", "1", "a"),
            Control("only-before", "gone", "x"),
            Control("second", "2", "b"),
            Control("third", "3", "c"));
        var after = Observe(Fixture,
            Control("third", "3!", "c"),
            Control("only-after", "new", "y"),
            Control("second", "2", "B"),
            Control("first", "1", "A"));

        Assert.Equal(new[]
        {
            new ControlChange("first", "Value", "a", "A"),
            new ControlChange("second", "Value", "b", "B"),
            new ControlChange("third", "Name", "3", "3!")
        }, ControlDiff.Compare(before, after));
    }

    [Fact]
    public void Compare_is_ordinal_and_case_sensitive()
    {
        var before = Observe(Fixture, Control("c", "Undo", "abc"));
        var after = Observe(Fixture, Control("c", "undo", "ABC"));

        Assert.Equal(new[]
        {
            new ControlChange("c", "Name", "Undo", "undo"),
            new ControlChange("c", "Value", "abc", "ABC")
        }, ControlDiff.Compare(before, after));
    }

    [Fact]
    public void Compare_matches_automation_ids_ordinally()
    {
        var before = Observe(Fixture, Control("Field", "n", "1"));
        var after = Observe(Fixture, Control("field", "n", "2"));

        Assert.Empty(ControlDiff.Compare(before, after));
    }

    [Fact]
    public void Compare_treats_null_values_as_distinct_from_text()
    {
        var before = Observe(Fixture, Control("appears", "n", null), Control("disappears", "n", "v"), Control("stays-null", "n", null), Control("empty", "n", ""));
        var after = Observe(Fixture, Control("appears", "n", "v"), Control("disappears", "n", null), Control("stays-null", "n", null), Control("empty", "n", null));

        Assert.Equal(new[]
        {
            new ControlChange("appears", "Value", null, "v"),
            new ControlChange("disappears", "Value", "v", null),
            new ControlChange("empty", "Value", "", null)
        }, ControlDiff.Compare(before, after));
    }

    // ---------- ActionReadback.Describe ----------

    [Fact]
    public void Describe_quotes_values()
    {
        Assert.Equal(
            "button-text.Value: \"Create account\" -> \"Sign up\"",
            ActionReadback.Describe(new ControlChange("button-text", "Value", "Create account", "Sign up")));
    }

    [Fact]
    public void Describe_renders_null_as_none()
    {
        Assert.Equal("x.Value: (none) -> \"v\"", ActionReadback.Describe(new ControlChange("x", "Value", null, "v")));
        Assert.Equal("x.Name: \"n\" -> (none)", ActionReadback.Describe(new ControlChange("x", "Name", "n", null)));
    }

    [Fact]
    public void Describe_quotes_empty_strings_rather_than_treating_them_as_none()
    {
        Assert.Equal("x.Value: \"\" -> \"a\"", ActionReadback.Describe(new ControlChange("x", "Value", "", "a")));
    }

    // ---------- ActionReadback.VerifyClick ----------

    [Fact]
    public void VerifyClick_reports_every_observed_change()
    {
        var before = Observe(Fixture, Button("design-button", "Create account"), Field("button-fill", "#2D6CDF"));
        var after = Observe(Fixture, Button("design-button", "Sign up"), Field("button-fill", "#1E8E3E"));

        var outcome = ActionReadback.VerifyClick(before, after);

        Assert.Equal(new ToolOutcome(
            true,
            true,
            "observed after click: design-button.Name: \"Create account\" -> \"Sign up\"; button-fill.Value: \"#2D6CDF\" -> \"#1E8E3E\""), outcome);
    }

    [Fact]
    public void VerifyClick_without_changes_is_partial_and_unverified()
    {
        var before = Observe(Fixture, Button("design-button", "Create account"));
        var after = Observe(Fixture, Button("design-button", "Create account"));

        var outcome = ActionReadback.VerifyClick(before, after);

        Assert.Equal(new ToolOutcome(false, true, "partial/unverified: the click was issued but no observed control changed."), outcome);
    }

    [Fact]
    public void VerifyClick_rejects_a_different_window()
    {
        var before = Observe(Fixture, Button("b", "x"));
        var after = Observe(Fixture with { Handle = 1 }, Button("b", "y"));

        Assert.Throws<ArgumentException>(() => ActionReadback.VerifyClick(before, after));
    }

    // ---------- ActionReadback.VerifyExpected ----------

    [Fact]
    public void VerifyExpected_succeeds_when_every_expected_state_matches()
    {
        var after = Observe(Fixture,
            Field("button-text", "Create account"),
            Button("design-button", "Create account"),
            Control("nullable", "n", null));

        var outcome = ActionReadback.VerifyExpected(after, new[]
        {
            new ExpectedControlState("button-text", "Value", "Create account"),
            new ExpectedControlState("design-button", "Name", "Create account"),
            new ExpectedControlState("nullable", "Value", null)
        }, "observed Undo restored");

        Assert.Equal(new ToolOutcome(
            true,
            true,
            "observed Undo restored: button-text.Value = \"Create account\"; design-button.Name = \"Create account\"; nullable.Value = (none)"), outcome);
    }

    [Fact]
    public void VerifyExpected_reports_a_missing_control()
    {
        var after = Observe(Fixture, Field("button-text", "Create account"));

        var outcome = ActionReadback.VerifyExpected(after, new[]
        {
            new ExpectedControlState("ghost", "Value", "x")
        }, "ok");

        Assert.Equal(new ToolOutcome(false, true, "partial/unverified: ghost is not visible"), outcome);
    }

    [Fact]
    public void VerifyExpected_reports_a_value_mismatch()
    {
        var after = Observe(Fixture, Field("button-text", "Sign up"));

        var outcome = ActionReadback.VerifyExpected(after, new[]
        {
            new ExpectedControlState("button-text", "Value", "Create account")
        }, "ok");

        Assert.Equal(new ToolOutcome(false, true, "partial/unverified: button-text.Value is \"Sign up\" but expected \"Create account\""), outcome);
    }

    [Fact]
    public void VerifyExpected_reports_a_name_mismatch()
    {
        var after = Observe(Fixture, Button("design-button", "Sign up"));

        var outcome = ActionReadback.VerifyExpected(after, new[]
        {
            new ExpectedControlState("design-button", "Name", "Create account")
        }, "ok");

        Assert.Equal(new ToolOutcome(false, true, "partial/unverified: design-button.Name is \"Sign up\" but expected \"Create account\""), outcome);
    }

    [Fact]
    public void VerifyExpected_renders_null_values_as_none_in_mismatches()
    {
        var after = Observe(Fixture, Control("a", "n", null), Control("b", "n", "v"));

        var first = ActionReadback.VerifyExpected(after, new[] { new ExpectedControlState("a", "Value", "x") }, "ok");
        var second = ActionReadback.VerifyExpected(after, new[] { new ExpectedControlState("b", "Value", null) }, "ok");

        Assert.Equal("partial/unverified: a.Value is (none) but expected \"x\"", first.Message);
        Assert.Equal("partial/unverified: b.Value is \"v\" but expected (none)", second.Message);
        Assert.False(first.Success);
        Assert.False(second.Success);
    }

    [Fact]
    public void VerifyExpected_reports_only_the_first_problem_in_expected_order()
    {
        var after = Observe(Fixture, Field("a", "1"), Field("b", "wrong"));

        var outcome = ActionReadback.VerifyExpected(after, new[]
        {
            new ExpectedControlState("a", "Value", "1"),
            new ExpectedControlState("b", "Value", "2"),
            new ExpectedControlState("missing", "Value", "3")
        }, "ok");

        Assert.Equal(new ToolOutcome(false, true, "partial/unverified: b.Value is \"wrong\" but expected \"2\""), outcome);
    }

    [Fact]
    public void VerifyExpected_is_ordinal()
    {
        var after = Observe(Fixture, Field("a", "create account"));

        var outcome = ActionReadback.VerifyExpected(after, new[] { new ExpectedControlState("a", "Value", "Create account") }, "ok");

        Assert.False(outcome.Success);
    }

    [Fact]
    public void VerifyExpected_rejects_an_empty_expectation_list()
    {
        var after = Observe(Fixture, Field("a", "1"));

        Assert.Throws<ArgumentException>(() => ActionReadback.VerifyExpected(after, Array.Empty<ExpectedControlState>(), "ok"));
    }

    // ---------- helpers ----------

    private static ControlCandidate Field(string id, string? value) =>
        Candidate(id, id, "Edit", value, canInvoke: false, canSetValue: true);

    private static ControlCandidate Button(string id, string name) =>
        Candidate(id, name, "Button", null, canInvoke: true, canSetValue: false);

    private static ControlCandidate Control(string id, string name, string? value) =>
        Candidate(id, name, "Custom", value, canInvoke: false, canSetValue: false);

    private static ControlCandidate Candidate(string id, string name, string type, string? value, bool canInvoke, bool canSetValue) =>
        new(id, name, type, value, new RectD(10, 10, 100, 30), new[] { 1, 2, 3 }, true, false, false, canInvoke, canSetValue);

    private static ControlObservation Observe(WindowIdentity target, params ControlCandidate[] candidates)
    {
        var context = new ScreenContext(
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            Generation: 3,
            new DisplayInfo("display", "Display", new RectD(0, 0, 1920, 1080), 1),
            1920,
            1080,
            target,
            new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
        var observation = ControlObservation.Create(context, candidates);
        Assert.Equal(candidates.Length, observation.Controls.Count);
        return observation;
    }
}
