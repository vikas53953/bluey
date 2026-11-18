using System.Linq;
using Boo.Core;

namespace Boo.Core.Tests;

public sealed class UndoJournalTests
{
    private const string ProcessName = "Boo.QaWorkspace";
    private const long Generation = 91;
    private static readonly WindowIdentity Window = new(4242, 77, ProcessName, new RectD(0, 0, 1000, 800));
    private static readonly WindowIdentity OtherWindow = new(5151, 88, "Other", new RectD(0, 0, 1000, 800));

    private const string NothingToUndo = "Nothing to undo: Boo has no recorded edit.";
    private const string WindowGone = "The edited window is no longer the current target.";
    private const string Ambiguous = "More than one Undo control is visible; Boo will not guess.";

    // ---------- empty journal ----------

    [Fact]
    public void A_new_journal_has_nothing_to_undo()
    {
        var journal = new UndoJournal();

        Assert.False(journal.HasPendingUndo);
        Assert.Null(journal.Window);
        AssertBlocked(journal.Plan(Observe(Window, Field("button-text", "x")), Generation), NothingToUndo);
    }

    // ---------- RecordEdit ----------

    [Fact]
    public void RecordEdit_rejects_invalid_arguments()
    {
        var journal = new UndoJournal();

        Assert.ThrowsAny<ArgumentException>(() => journal.RecordEdit(null!, "button-text", "a", "b"));
        Assert.ThrowsAny<ArgumentException>(() => journal.RecordEdit(Window, null!, "a", "b"));
        Assert.ThrowsAny<ArgumentException>(() => journal.RecordEdit(Window, "", "a", "b"));
        Assert.ThrowsAny<ArgumentException>(() => journal.RecordEdit(Window, "   ", "a", "b"));
        Assert.ThrowsAny<ArgumentException>(() => journal.RecordEdit(Window, "button-text", "a", null!));
        Assert.False(journal.HasPendingUndo);
    }

    [Fact]
    public void RecordEdit_marks_a_pending_undo_for_the_window()
    {
        var journal = new UndoJournal();

        journal.RecordEdit(Window, "button-text", "Create account", "Sign up");

        Assert.True(journal.HasPendingUndo);
        Assert.Equal(Window, journal.Window);
    }

    [Fact]
    public void RecordEdit_for_a_different_window_discards_the_previous_records()
    {
        var journal = new UndoJournal();
        journal.RecordEdit(Window, "button-text", "Create account", "Sign up");
        journal.RecordEdit(OtherWindow, "title", "Old", "New");

        Assert.Equal(OtherWindow, journal.Window);
        var plan = journal.Plan(Observe(OtherWindow, Field("title", "New")), Generation);

        Assert.True(plan.CanUndo);
        Assert.Equal(new[] { new ExpectedControlState("title", "Value", "Old") }, plan.Expected);
        var step = Assert.Single(plan.Steps);
        AssertTypeText(step, "Other", "title", "Old");
    }

    [Fact]
    public void RecordEdit_for_the_same_window_after_it_moved_keeps_previous_records()
    {
        var journal = new UndoJournal();
        var moved = Window with { Bounds = new RectD(5, 5, 900, 700) };
        journal.RecordEdit(Window, "a", "A0", "A1");
        journal.RecordEdit(moved, "b", "B0", "B1");

        var plan = journal.Plan(Observe(moved, Field("a", "A1"), Field("b", "B1")), Generation);

        Assert.True(plan.CanUndo);
        Assert.Equal(new[]
        {
            new ExpectedControlState("a", "Value", "A0"),
            new ExpectedControlState("b", "Value", "B0")
        }, plan.Expected);
    }

    [Fact]
    public void Repeated_edits_of_a_field_keep_the_first_before_and_the_latest_after()
    {
        var journal = new UndoJournal();
        journal.RecordEdit(Window, "button-text", "Create account", "Sign");
        journal.RecordEdit(Window, "button-text", "Sign", "Sign up");

        var plan = journal.Plan(Observe(Window, Field("button-text", "Sign up")), Generation);

        Assert.True(plan.CanUndo);
        var step = Assert.Single(plan.Steps);
        AssertTypeText(step, ProcessName, "button-text", "Create account");
        Assert.Equal(new[] { new ExpectedControlState("button-text", "Value", "Create account") }, plan.Expected);

        var stale = journal.Plan(Observe(Window, Field("button-text", "Sign")), Generation);
        AssertBlocked(stale, "button-text changed since Boo edited it; Undo would overwrite a newer change.");
    }

    [Fact]
    public void An_edit_after_an_apply_starts_a_new_transaction()
    {
        var journal = new UndoJournal();
        journal.RecordEdit(Window, "button-text", "Create account", "Sign up");
        journal.RecordApply(Window, "apply-edit", new[] { new ControlChange("design-button", "Name", "Create account", "Sign up") });
        journal.RecordEdit(Window, "button-fill", "#2D6CDF", "#1E8E3E");

        // The undo control is visible, but no apply belongs to the new transaction, so the fields-only path is used.
        var current = Observe(Window,
            Field("button-text", "Sign up"),
            Field("button-fill", "#1E8E3E"),
            Button("apply-edit", "Apply edit"),
            Button("undo-edit", "Undo"),
            Button("design-button", "Sign up"));
        var plan = journal.Plan(current, Generation);

        Assert.True(plan.CanUndo);
        var step = Assert.Single(plan.Steps);
        AssertTypeText(step, ProcessName, "button-fill", "#2D6CDF");
        Assert.Equal(new[] { new ExpectedControlState("button-fill", "Value", "#2D6CDF") }, plan.Expected);
    }

    // ---------- RecordApply ----------

    [Fact]
    public void RecordApply_with_no_observed_changes_is_ignored()
    {
        var journal = new UndoJournal();

        journal.RecordApply(Window, "apply-edit", Array.Empty<ControlChange>());

        Assert.False(journal.HasPendingUndo);
        AssertBlocked(journal.Plan(Observe(Window, Button("undo-edit", "Undo")), Generation), NothingToUndo);
    }

    [Fact]
    public void RecordApply_with_no_observed_changes_leaves_the_edits_on_the_fields_only_path()
    {
        var journal = new UndoJournal();
        journal.RecordEdit(Window, "button-text", "Create account", "Sign up");
        journal.RecordApply(Window, "apply-edit", Array.Empty<ControlChange>());

        var plan = journal.Plan(Observe(Window, Field("button-text", "Sign up"), Button("undo-edit", "Undo"), Button("apply-edit", "Apply edit")), Generation);

        Assert.True(plan.CanUndo);
        var step = Assert.Single(plan.Steps);
        AssertTypeText(step, ProcessName, "button-text", "Create account");
    }

    [Fact]
    public void RecordApply_alone_marks_a_pending_undo()
    {
        var journal = new UndoJournal();

        journal.RecordApply(Window, "apply-edit", new[] { new ControlChange("design-button", "Name", "Create account", "Sign up") });

        Assert.True(journal.HasPendingUndo);
        Assert.Equal(Window, journal.Window);
    }

    [Fact]
    public void RecordApply_for_a_different_window_discards_earlier_edits()
    {
        var journal = new UndoJournal();
        journal.RecordEdit(Window, "button-text", "Create account", "Sign up");
        journal.RecordApply(OtherWindow, "apply", new[] { new ControlChange("label", "Name", "Before", "After") });

        Assert.Equal(OtherWindow, journal.Window);
        var plan = journal.Plan(Observe(OtherWindow, Button("label", "After"), Button("undo", "Undo"), Button("apply", "Apply")), Generation);

        Assert.True(plan.CanUndo);
        Assert.Equal(new[] { new ExpectedControlState("label", "Name", "Before") }, plan.Expected);
        var step = Assert.Single(plan.Steps);
        AssertClick(step, "Other", "undo", "Undo");
    }

    [Fact]
    public void A_second_apply_merges_keeping_the_earliest_before_and_the_latest_after()
    {
        var journal = new UndoJournal();
        journal.RecordEdit(Window, "button-text", "Create account", "Join now");
        journal.RecordApply(Window, "apply-edit", new[]
        {
            new ControlChange("design-button", "Name", "Create account", "Sign up")
        });
        journal.RecordApply(Window, "apply-edit", new[]
        {
            new ControlChange("design-button", "Name", "Sign up", "Join now"),
            new ControlChange("preview", "Value", "old preview", "new preview")
        });

        var current = Observe(Window,
            Field("button-text", "Join now"),
            Button("design-button", "Join now"),
            Control("preview", "Preview", "new preview"),
            Button("apply-edit", "Apply edit"),
            Button("undo-edit", "Undo"));
        var plan = journal.Plan(current, Generation);

        Assert.True(plan.CanUndo);
        Assert.Equal(new[]
        {
            new ExpectedControlState("button-text", "Value", "Create account"),
            new ExpectedControlState("design-button", "Name", "Create account"),
            new ExpectedControlState("preview", "Value", "old preview")
        }, plan.Expected);

        // The merged change's After is the latest one, so the earlier After now counts as a conflict.
        var stale = Observe(Window,
            Field("button-text", "Join now"),
            Button("design-button", "Sign up"),
            Control("preview", "Preview", "new preview"),
            Button("apply-edit", "Apply edit"),
            Button("undo-edit", "Undo"));
        AssertBlocked(journal.Plan(stale, Generation), "design-button changed since Boo edited it; Undo would overwrite a newer change.");
    }

    // ---------- Plan: blocked reasons ----------

    [Fact]
    public void Plan_is_blocked_when_the_current_target_is_a_different_window()
    {
        var journal = new UndoJournal();
        journal.RecordEdit(Window, "button-text", "Create account", "Sign up");

        AssertBlocked(journal.Plan(Observe(OtherWindow, Field("button-text", "Sign up")), Generation), WindowGone);
        AssertBlocked(journal.Plan(Observe(Window with { ProcessId = 1 }, Field("button-text", "Sign up")), Generation), WindowGone);
        AssertBlocked(journal.Plan(Observe(Window with { Handle = 1 }, Field("button-text", "Sign up")), Generation), WindowGone);
        Assert.True(journal.HasPendingUndo);
    }

    [Fact]
    public void Plan_accepts_the_same_window_with_new_bounds()
    {
        var journal = new UndoJournal();
        journal.RecordEdit(Window, "button-text", "Create account", "Sign up");

        var plan = journal.Plan(Observe(Window with { Bounds = new RectD(5, 5, 900, 700) }, Field("button-text", "Sign up")), Generation);

        Assert.True(plan.CanUndo);
        Assert.Null(plan.BlockedReason);
    }

    [Fact]
    public void Plan_is_blocked_when_an_edited_field_changed_since_the_edit()
    {
        var journal = new UndoJournal();
        journal.RecordEdit(Window, "button-text", "Create account", "Sign up");

        AssertBlocked(
            journal.Plan(Observe(Window, Field("button-text", "Sign up now")), Generation),
            "button-text changed since Boo edited it; Undo would overwrite a newer change.");
    }

    [Fact]
    public void Plan_treats_a_missing_edited_field_as_a_conflict()
    {
        var journal = new UndoJournal();
        journal.RecordEdit(Window, "button-text", "Create account", "Sign up");

        AssertBlocked(
            journal.Plan(Observe(Window, Field("something-else", "Sign up")), Generation),
            "button-text changed since Boo edited it; Undo would overwrite a newer change.");
    }

    [Fact]
    public void Plan_reports_the_first_conflicting_field_in_recording_order_before_apply_changes()
    {
        var journal = new UndoJournal();
        journal.RecordEdit(Window, "button-text", "Create account", "Sign up");
        journal.RecordEdit(Window, "button-fill", "#2D6CDF", "#1E8E3E");
        journal.RecordApply(Window, "apply-edit", new[] { new ControlChange("design-button", "Name", "Create account", "Sign up") });

        var current = Observe(Window,
            Field("button-text", "Sign up"),
            Field("button-fill", "#000000"),
            Button("design-button", "Changed elsewhere"),
            Button("apply-edit", "Apply edit"),
            Button("undo-edit", "Undo"));

        AssertBlocked(journal.Plan(current, Generation), "button-fill changed since Boo edited it; Undo would overwrite a newer change.");
    }

    [Fact]
    public void Plan_is_blocked_when_an_applied_change_no_longer_holds()
    {
        var journal = new UndoJournal();
        journal.RecordEdit(Window, "button-text", "Create account", "Sign up");
        journal.RecordApply(Window, "apply-edit", new[] { new ControlChange("design-button", "Name", "Create account", "Sign up") });

        var current = Observe(Window,
            Field("button-text", "Sign up"),
            Button("design-button", "Something newer"),
            Button("apply-edit", "Apply edit"),
            Button("undo-edit", "Undo"));

        AssertBlocked(journal.Plan(current, Generation), "design-button changed since Boo edited it; Undo would overwrite a newer change.");
    }

    [Fact]
    public void Plan_treats_a_missing_applied_control_as_a_conflict()
    {
        var journal = new UndoJournal();
        journal.RecordApply(Window, "apply-edit", new[] { new ControlChange("design-button", "Name", "Create account", "Sign up") });

        var current = Observe(Window, Button("apply-edit", "Apply edit"), Button("undo-edit", "Undo"));

        AssertBlocked(journal.Plan(current, Generation), "design-button changed since Boo edited it; Undo would overwrite a newer change.");
    }

    [Fact]
    public void Plan_checks_applied_value_changes_with_null_awareness()
    {
        var journal = new UndoJournal();
        journal.RecordApply(Window, "apply-edit", new[] { new ControlChange("status", "Value", null, "Saved") });

        var plan = journal.Plan(Observe(Window, Control("status", "Status", "Saved"), Button("undo-edit", "Undo")), Generation);
        Assert.True(plan.CanUndo);
        Assert.Equal(new[] { new ExpectedControlState("status", "Value", null) }, plan.Expected);

        AssertBlocked(
            journal.Plan(Observe(Window, Control("status", "Status", null), Button("undo-edit", "Undo")), Generation),
            "status changed since Boo edited it; Undo would overwrite a newer change.");
    }

    [Fact]
    public void Plan_is_blocked_when_more_than_one_undo_control_is_visible()
    {
        var journal = RecordedFixtureJournal();

        var current = AfterApply(extra: new[]
        {
            Candidate("history-UNDO", "Step back", "Button", null, canInvoke: true, canSetValue: false)
        });

        AssertBlocked(journal.Plan(current, Generation), Ambiguous);
    }

    [Fact]
    public void Plan_counts_controls_named_undo_ignoring_case_as_undo_controls()
    {
        var journal = RecordedFixtureJournal();

        var current = AfterApply(extra: new[]
        {
            Candidate("toolbar-revert", "UNDO", "Button", null, canInvoke: true, canSetValue: false)
        });

        AssertBlocked(journal.Plan(current, Generation), Ambiguous);
    }

    [Fact]
    public void Plan_ignores_non_invokable_controls_when_looking_for_the_undo_control()
    {
        var journal = RecordedFixtureJournal();

        var current = AfterApply(extra: new[]
        {
            Candidate("undo-hint", "Undo", "Text", null, canInvoke: false, canSetValue: false)
        });
        var plan = journal.Plan(current, Generation);

        Assert.True(plan.CanUndo);
        var step = Assert.Single(plan.Steps);
        AssertClick(step, ProcessName, "undo-edit", "Undo");
    }

    [Fact]
    public void Plan_checks_conflicts_before_an_ambiguous_undo_control()
    {
        var journal = RecordedFixtureJournal();

        var current = Observe(Window,
            Field("button-text", "Edited elsewhere"),
            Field("button-fill", "#1E8E3E"),
            Field("button-ink", "#FFFFFF"),
            Button("apply-edit", "Apply edit"),
            Button("undo-edit", "Undo"),
            Button("undo-other", "Undo"),
            Button("design-button", "Sign up"));

        AssertBlocked(journal.Plan(current, Generation), "button-text changed since Boo edited it; Undo would overwrite a newer change.");
    }

    [Fact]
    public void Plan_checks_an_ambiguous_undo_control_before_an_empty_before_value()
    {
        var journal = new UndoJournal();
        journal.RecordEdit(Window, "note", "", "Hello");
        journal.RecordApply(Window, "apply-edit", new[] { new ControlChange("preview", "Name", "", "Hello") });

        var current = Observe(Window,
            Field("note", "Hello"),
            Control("preview", "Hello", null),
            Button("apply-edit", "Apply edit"),
            Button("undo-a", "Undo"),
            Button("undo-b", "Undo"));

        AssertBlocked(journal.Plan(current, Generation), Ambiguous);
    }

    [Fact]
    public void Fields_only_path_cannot_restore_an_empty_value()
    {
        var journal = new UndoJournal();
        journal.RecordEdit(Window, "button-text", "Create account", "Sign up");
        journal.RecordEdit(Window, "note", "", "Hello");

        AssertBlocked(
            journal.Plan(Observe(Window, Field("button-text", "Sign up"), Field("note", "Hello")), Generation),
            "note was empty before the edit; Boo cannot restore an empty value.");
    }

    [Fact]
    public void Fields_only_path_cannot_restore_an_unknown_null_value()
    {
        var journal = new UndoJournal();
        journal.RecordEdit(Window, "note", null, "Hello");

        AssertBlocked(
            journal.Plan(Observe(Window, Field("note", "Hello")), Generation),
            "note was empty before the edit; Boo cannot restore an empty value.");
    }

    [Fact]
    public void Reapply_path_cannot_restore_an_empty_value()
    {
        var journal = new UndoJournal();
        journal.RecordEdit(Window, "note", "", "Hello");
        journal.RecordApply(Window, "apply-edit", new[] { new ControlChange("preview", "Name", "", "Hello") });

        var current = Observe(Window, Field("note", "Hello"), Control("preview", "Hello", null), Button("apply-edit", "Apply edit"));

        AssertBlocked(journal.Plan(current, Generation), "note was empty before the edit; Boo cannot restore an empty value.");
    }

    [Fact]
    public void App_undo_path_can_restore_a_field_that_was_empty()
    {
        var journal = new UndoJournal();
        journal.RecordEdit(Window, "note", "", "Hello");
        journal.RecordApply(Window, "apply-edit", new[] { new ControlChange("preview", "Name", "", "Hello") });

        var current = Observe(Window, Field("note", "Hello"), Control("preview", "Hello", null), Button("apply-edit", "Apply edit"), Button("undo-edit", "Undo"));
        var plan = journal.Plan(current, Generation);

        Assert.True(plan.CanUndo);
        AssertClick(Assert.Single(plan.Steps), ProcessName, "undo-edit", "Undo");
        Assert.Equal(new[]
        {
            new ExpectedControlState("note", "Value", ""),
            new ExpectedControlState("preview", "Name", "")
        }, plan.Expected);
    }

    // ---------- Plan: paths ----------

    [Fact]
    public void App_undo_path_clicks_the_single_undo_control()
    {
        var journal = RecordedFixtureJournal();

        var plan = journal.Plan(AfterApply(), Generation);

        Assert.True(plan.CanUndo);
        Assert.Null(plan.BlockedReason);
        var step = Assert.Single(plan.Steps);
        AssertClick(step, ProcessName, "undo-edit", "Undo");
        Assert.Equal(new[]
        {
            new ExpectedControlState("button-text", "Value", "Create account"),
            new ExpectedControlState("button-fill", "Value", "#2D6CDF"),
            new ExpectedControlState("design-button", "Name", "Create account")
        }, plan.Expected);
    }

    [Fact]
    public void App_undo_path_matches_an_automation_id_containing_undo_ignoring_case()
    {
        var journal = new UndoJournal();
        journal.RecordApply(Window, "apply-edit", new[] { new ControlChange("design-button", "Name", "Create account", "Sign up") });

        var current = Observe(Window, Button("design-button", "Sign up"), Button("btnUndoLast", "Step back"), Button("apply-edit", "Apply edit"));
        var plan = journal.Plan(current, Generation);

        Assert.True(plan.CanUndo);
        AssertClick(Assert.Single(plan.Steps), ProcessName, "btnUndoLast", "Step back");
    }

    [Fact]
    public void Reapply_path_types_originals_in_reverse_order_then_clicks_apply()
    {
        var journal = RecordedFixtureJournal();

        var current = Observe(Window,
            Field("button-text", "Sign up"),
            Field("button-fill", "#1E8E3E"),
            Field("button-ink", "#FFFFFF"),
            Button("apply-edit", "Apply edit"),
            Button("design-button", "Sign up"));
        var plan = journal.Plan(current, Generation);

        Assert.True(plan.CanUndo);
        Assert.Null(plan.BlockedReason);
        Assert.Equal(3, plan.Steps.Count);
        AssertTypeText(plan.Steps[0], ProcessName, "button-fill", "#2D6CDF");
        AssertTypeText(plan.Steps[1], ProcessName, "button-text", "Create account");
        AssertClick(plan.Steps[2], ProcessName, "apply-edit", "Apply edit");
        Assert.Equal(new[]
        {
            new ExpectedControlState("button-text", "Value", "Create account"),
            new ExpectedControlState("button-fill", "Value", "#2D6CDF"),
            new ExpectedControlState("design-button", "Name", "Create account")
        }, plan.Expected);
    }

    [Fact]
    public void Reapply_path_is_blocked_when_the_apply_control_is_not_visible()
    {
        var journal = RecordedFixtureJournal();

        var current = Observe(Window,
            Field("button-text", "Sign up"),
            Field("button-fill", "#1E8E3E"),
            Button("design-button", "Sign up"));

        AssertBlocked(journal.Plan(current, Generation), "apply-edit is not visible; Boo cannot re-apply the original values.");
    }

    [Fact]
    public void Fields_only_path_types_originals_in_reverse_recording_order()
    {
        var journal = new UndoJournal();
        journal.RecordEdit(Window, "button-text", "Create account", "Sign up");
        journal.RecordEdit(Window, "button-fill", "#2D6CDF", "#1E8E3E");
        journal.RecordEdit(Window, "button-ink", "#FFFFFF", "#000000");

        var current = Observe(Window,
            Field("button-text", "Sign up"),
            Field("button-fill", "#1E8E3E"),
            Field("button-ink", "#000000"));
        var plan = journal.Plan(current, 1234);

        Assert.True(plan.CanUndo);
        Assert.Null(plan.BlockedReason);
        Assert.Equal(3, plan.Steps.Count);
        AssertTypeText(plan.Steps[0], ProcessName, "button-ink", "#FFFFFF", 1234);
        AssertTypeText(plan.Steps[1], ProcessName, "button-fill", "#2D6CDF", 1234);
        AssertTypeText(plan.Steps[2], ProcessName, "button-text", "Create account", 1234);
        Assert.Equal(new[]
        {
            new ExpectedControlState("button-text", "Value", "Create account"),
            new ExpectedControlState("button-fill", "Value", "#2D6CDF"),
            new ExpectedControlState("button-ink", "Value", "#FFFFFF")
        }, plan.Expected);
    }

    [Fact]
    public void Repeated_edits_keep_their_first_recording_position()
    {
        var journal = new UndoJournal();
        journal.RecordEdit(Window, "a", "A0", "A1");
        journal.RecordEdit(Window, "b", "B0", "B1");
        journal.RecordEdit(Window, "a", "A1", "A2");

        var plan = journal.Plan(Observe(Window, Field("a", "A2"), Field("b", "B1")), Generation);

        Assert.True(plan.CanUndo);
        Assert.Equal(new[]
        {
            new ExpectedControlState("a", "Value", "A0"),
            new ExpectedControlState("b", "Value", "B0")
        }, plan.Expected);
        AssertTypeText(plan.Steps[0], ProcessName, "b", "B0");
        AssertTypeText(plan.Steps[1], ProcessName, "a", "A0");
    }

    [Fact]
    public void Steps_target_the_current_process_name()
    {
        var journal = new UndoJournal();
        journal.RecordEdit(Window, "a", "A0", "A1");

        var renamed = Window with { ProcessName = "Boo.QaWorkspace.Renamed" };
        var plan = journal.Plan(Observe(renamed, Field("a", "A1")), 7);

        Assert.True(plan.CanUndo);
        AssertTypeText(Assert.Single(plan.Steps), "Boo.QaWorkspace.Renamed", "a", "A0", 7);
    }

    [Fact]
    public void Expected_skips_apply_changes_already_listed_for_an_edited_field()
    {
        var journal = new UndoJournal();
        journal.RecordEdit(Window, "button-text", "Create account", "Sign up");
        journal.RecordApply(Window, "apply-edit", new[]
        {
            new ControlChange("button-text", "Value", "Create account", "Sign up"),
            new ControlChange("button-text", "Name", "Label", "Label!"),
            new ControlChange("design-button", "Name", "Create account", "Sign up")
        });

        var current = Observe(Window,
            Candidate("button-text", "Label!", "Edit", "Sign up", canInvoke: false, canSetValue: true),
            Button("design-button", "Sign up"),
            Button("apply-edit", "Apply edit"),
            Button("undo-edit", "Undo"));
        var plan = journal.Plan(current, Generation);

        Assert.True(plan.CanUndo);
        Assert.Equal(new[]
        {
            new ExpectedControlState("button-text", "Value", "Create account"),
            new ExpectedControlState("button-text", "Name", "Label"),
            new ExpectedControlState("design-button", "Name", "Create account")
        }, plan.Expected);
    }

    // ---------- VerifyUndo / Clear ----------

    [Fact]
    public void VerifyUndo_rejects_a_blocked_plan()
    {
        var journal = new UndoJournal();
        var blocked = journal.Plan(Observe(Window, Field("a", "x")), Generation);

        Assert.False(blocked.CanUndo);
        Assert.Throws<ArgumentException>(() => journal.VerifyUndo(Observe(Window, Field("a", "x")), blocked));
    }

    [Fact]
    public void VerifyUndo_success_reports_restored_state_and_clears_the_journal()
    {
        var journal = RecordedFixtureJournal();
        var plan = journal.Plan(AfterApply(), Generation);

        var outcome = journal.VerifyUndo(Original(), plan);

        Assert.Equal(new ToolOutcome(
            true,
            true,
            "observed Undo restored: button-text.Value = \"Create account\"; button-fill.Value = \"#2D6CDF\"; design-button.Name = \"Create account\""), outcome);
        Assert.False(journal.HasPendingUndo);
        Assert.Null(journal.Window);
        AssertBlocked(journal.Plan(Original(), Generation), NothingToUndo);
    }

    [Fact]
    public void VerifyUndo_failure_keeps_the_journal()
    {
        var journal = RecordedFixtureJournal();
        var plan = journal.Plan(AfterApply(), Generation);

        var outcome = journal.VerifyUndo(AfterApply(), plan);

        Assert.Equal(new ToolOutcome(false, true, "partial/unverified: button-text.Value is \"Sign up\" but expected \"Create account\""), outcome);
        Assert.True(journal.HasPendingUndo);
        Assert.Equal(Window, journal.Window);
        Assert.True(journal.Plan(AfterApply(), Generation).CanUndo);
    }

    [Fact]
    public void VerifyUndo_failure_when_a_restored_control_is_missing()
    {
        var journal = RecordedFixtureJournal();
        var plan = journal.Plan(AfterApply(), Generation);

        var restoredButMissingDesign = Observe(Window,
            Field("button-text", "Create account"),
            Field("button-fill", "#2D6CDF"),
            Button("apply-edit", "Apply edit"),
            Button("undo-edit", "Undo"));
        var outcome = journal.VerifyUndo(restoredButMissingDesign, plan);

        Assert.Equal(new ToolOutcome(false, true, "partial/unverified: design-button is not visible"), outcome);
        Assert.True(journal.HasPendingUndo);
    }

    [Fact]
    public void Clear_drops_everything()
    {
        var journal = RecordedFixtureJournal();

        journal.Clear();

        Assert.False(journal.HasPendingUndo);
        Assert.Null(journal.Window);
        AssertBlocked(journal.Plan(AfterApply(), Generation), NothingToUndo);
    }

    // ---------- end to end (QA fixture) ----------

    [Fact]
    public void QaFixture_edit_apply_then_undo_is_planned_and_verified()
    {
        var journal = new UndoJournal();
        var initial = Original();

        // Type "Sign up" into button-text: record with the value from the pre-action observation.
        journal.RecordEdit(initial.Context.Target, "button-text", ValueOf(initial, "button-text"), "Sign up");
        var afterText = Observe(Window,
            Field("button-text", "Sign up"),
            Field("button-fill", "#2D6CDF"),
            Field("button-ink", "#FFFFFF"),
            Button("apply-edit", "Apply edit"),
            Button("undo-edit", "Undo"),
            Button("design-button", "Create account"));

        // Type "#1E8E3E" into button-fill.
        journal.RecordEdit(afterText.Context.Target, "button-fill", ValueOf(afterText, "button-fill"), "#1E8E3E");
        var beforeClick = Observe(Window,
            Field("button-text", "Sign up"),
            Field("button-fill", "#1E8E3E"),
            Field("button-ink", "#FFFFFF"),
            Button("apply-edit", "Apply edit"),
            Button("undo-edit", "Undo"),
            Button("design-button", "Create account"));
        Assert.True(journal.HasPendingUndo);

        // Click Apply edit and read back.
        var afterClick = AfterApply();
        var click = ActionReadback.VerifyClick(beforeClick, afterClick);
        Assert.Equal(new ToolOutcome(true, true, "observed after click: design-button.Name: \"Create account\" -> \"Sign up\""), click);
        var changes = ControlDiff.Compare(beforeClick, afterClick);
        journal.RecordApply(afterClick.Context.Target, "apply-edit", changes);

        // "undo": the plan is a single click on the app's Undo button.
        var plan = journal.Plan(afterClick, 512);
        Assert.True(plan.CanUndo);
        Assert.Null(plan.BlockedReason);
        var step = Assert.Single(plan.Steps);
        AssertClick(step, ProcessName, "undo-edit", "Undo", 512);

        // Read back after Undo: originals restored.
        var outcome = journal.VerifyUndo(Original(), plan);
        Assert.True(outcome.Success);
        Assert.True(outcome.Committed);
        Assert.Equal(
            "observed Undo restored: button-text.Value = \"Create account\"; button-fill.Value = \"#2D6CDF\"; design-button.Name = \"Create account\"",
            outcome.Message);
        Assert.False(journal.HasPendingUndo);
    }

    // ---------- helpers ----------

    private static UndoJournal RecordedFixtureJournal()
    {
        var journal = new UndoJournal();
        journal.RecordEdit(Window, "button-text", "Create account", "Sign up");
        journal.RecordEdit(Window, "button-fill", "#2D6CDF", "#1E8E3E");
        journal.RecordApply(Window, "apply-edit", new[] { new ControlChange("design-button", "Name", "Create account", "Sign up") });
        return journal;
    }

    private static ControlObservation Original() => Observe(Window,
        Field("button-text", "Create account"),
        Field("button-fill", "#2D6CDF"),
        Field("button-ink", "#FFFFFF"),
        Button("apply-edit", "Apply edit"),
        Button("undo-edit", "Undo"),
        Button("design-button", "Create account"));

    private static ControlObservation AfterApply(IEnumerable<ControlCandidate>? extra = null)
    {
        var candidates = new List<ControlCandidate>
        {
            Field("button-text", "Sign up"),
            Field("button-fill", "#1E8E3E"),
            Field("button-ink", "#FFFFFF"),
            Button("apply-edit", "Apply edit"),
            Button("undo-edit", "Undo"),
            Button("design-button", "Sign up")
        };
        if (extra is not null)
            candidates.AddRange(extra);
        return Observe(Window, candidates.ToArray());
    }

    private static string? ValueOf(ControlObservation observation, string id) =>
        observation.Controls.Single(c => c.AutomationId == id).Value;

    private static void AssertBlocked(UndoPlan plan, string reason)
    {
        Assert.False(plan.CanUndo);
        Assert.Equal(reason, plan.BlockedReason);
        Assert.Empty(plan.Steps);
        Assert.Empty(plan.Expected);
    }

    private static void AssertTypeText(ToolRequest step, string target, string id, string text, long generation = Generation)
    {
        Assert.Equal("type_text", step.Name);
        Assert.Equal(target, step.Target);
        Assert.Equal(generation, step.Generation);
        AssertArguments(step, ("automationId", id), ("text", text));
    }

    private static void AssertClick(ToolRequest step, string target, string id, string name, long generation = Generation)
    {
        Assert.Equal("click", step.Name);
        Assert.Equal(target, step.Target);
        Assert.Equal(generation, step.Generation);
        AssertArguments(step, ("automationId", id), ("name", name));
    }

    private static void AssertArguments(ToolRequest step, params (string Key, string Value)[] expected)
    {
        Assert.Equal(expected.Length, step.Arguments.Count);
        foreach (var (key, value) in expected)
        {
            Assert.True(step.Arguments.TryGetValue(key, out var actual), $"missing argument {key}");
            Assert.Equal(value, actual);
        }
    }

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
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            Generation: 5,
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
