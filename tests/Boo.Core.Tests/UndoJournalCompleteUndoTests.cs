using Boo.Core;

namespace Boo.Core.Tests;

/// <summary>
/// Issue #157 / PR #158: the single place an executed field-Undo step is settled. When Boo cannot re-read the window
/// after the Undo ran, the result is not a success, keeps the executed step's committed flag, and keeps the recorded
/// edit, so a later "undo" still knows what Boo changed. When the re-read shows the restore, the journal is consumed and
/// the next "undo" says there is nothing to undo.
/// </summary>
public sealed class UndoJournalCompleteUndoTests
{
    private const long Generation = 91;
    private const string NothingToUndo = "Nothing to undo: Boo has no recorded edit.";
    private static readonly WindowIdentity Window = new(4242, 77, "Boo.TypeTarget.App", new RectD(0, 0, 1000, 800));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UndoThatCannotBeReRead_IsUnverified_KeepsCommitted_AndKeepsTheRecordedEdit(bool committed)
    {
        var journal = new UndoJournal();
        journal.RecordEdit(Window, "FieldA", "ORIGINAL", "EDITED");
        var plan = journal.Plan(Observe(Field("FieldA", "EDITED")), Generation);
        Assert.True(plan.CanUndo);
        var executed = new ToolOutcome(true, committed, "observed UI Automation value read back exactly after typing.");

        var result = journal.CompleteUndo(executed, after: null, plan);

        Assert.False(result.Success);
        Assert.Equal(committed, result.Committed);
        Assert.Equal(UndoJournal.UnverifiedUndoMessage, result.Message);
        Assert.StartsWith("partial/unverified", result.Message, StringComparison.Ordinal);
        Assert.True(journal.HasPendingUndo, "an Undo Boo could not confirm must keep the recorded edit");
        Assert.Equal(Window, journal.Window);
        var again = journal.Plan(Observe(Field("FieldA", "EDITED")), Generation);
        Assert.True(again.CanUndo, again.BlockedReason);
    }

    [Fact]
    public void UndoThatIsReReadRestored_ConsumesTheJournal_SoTheNextUndoHasNothingToUndo()
    {
        var journal = new UndoJournal();
        journal.RecordEdit(Window, "FieldA", "ORIGINAL", "EDITED");
        var plan = journal.Plan(Observe(Field("FieldA", "EDITED")), Generation);
        var executed = new ToolOutcome(true, true, "observed UI Automation value read back exactly after typing.");

        var result = journal.CompleteUndo(executed, Observe(Field("FieldA", "ORIGINAL")), plan);

        Assert.True(result.Success, result.Message);
        Assert.True(result.Committed);
        Assert.False(journal.HasPendingUndo);
        var next = journal.Plan(Observe(Field("FieldA", "ORIGINAL")), Generation);
        Assert.False(next.CanUndo);
        Assert.Equal(NothingToUndo, next.BlockedReason);
    }

    [Fact]
    public void UndoReReadShowingAnotherValue_IsNotASuccess_AndKeepsTheRecordedEdit()
    {
        var journal = new UndoJournal();
        journal.RecordEdit(Window, "FieldA", "ORIGINAL", "EDITED");
        var plan = journal.Plan(Observe(Field("FieldA", "EDITED")), Generation);
        var executed = new ToolOutcome(true, true, "observed UI Automation value read back exactly after typing.");

        var result = journal.CompleteUndo(executed, Observe(Field("FieldA", "SOMETHING ELSE")), plan);

        Assert.False(result.Success);
        Assert.True(journal.HasPendingUndo);
    }

    private static ControlCandidate Field(string id, string? value) =>
        new(id, id, "Edit", value, new RectD(10, 10, 100, 30), new[] { 1, 2, 3 }, true, false, false, false, true);

    private static ControlObservation Observe(params ControlCandidate[] candidates)
    {
        var context = new ScreenContext(
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            Generation: 5,
            new DisplayInfo("display", "Display", new RectD(0, 0, 1920, 1080), 1),
            1920,
            1080,
            Window,
            new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
        return ControlObservation.Create(context, candidates);
    }
}
