using System.Text;
using Boo.Core;

namespace Boo.Core.Tests;

public sealed class FileUndoJournalTests
{
    private const long Generation = 17;
    private static readonly byte[] Before = Encoding.UTF8.GetBytes("<Window Title=\"Before\" />\n");
    private const string After = "<Window Title=\"After\" />\n";

    [Fact]
    public void A_new_journal_has_nothing_to_undo()
    {
        var journal = new FileUndoJournal();

        Assert.False(journal.HasPendingUndo);
        var plan = journal.Plan(Generation, () => Before);
        Assert.False(plan.CanUndo);
        Assert.Equal(FileUndoJournal.NothingRecorded, plan.BlockedReason);
        Assert.Null(plan.Step);
    }

    [Fact]
    public void Record_rejects_invalid_arguments()
    {
        var journal = new FileUndoJournal();

        Assert.ThrowsAny<ArgumentException>(() => journal.Record("", Before, After));
        Assert.ThrowsAny<ArgumentException>(() => journal.Record("  ", Before, After));
        Assert.ThrowsAny<ArgumentException>(() => journal.Record("a.txt", null, null));
        Assert.False(journal.HasPendingUndo);
    }

    [Fact]
    public void Overwrite_plans_a_write_of_the_previous_text_with_overwrite()
    {
        var journal = new FileUndoJournal();
        journal.Record("MainWindow.xaml", Before, After);

        var plan = journal.Plan(Generation, () => Encoding.UTF8.GetBytes(After));

        Assert.True(plan.CanUndo);
        Assert.Null(plan.BlockedReason);
        var step = Assert.IsType<ToolRequest>(plan.Step);
        Assert.Equal("write_file", step.Name);
        Assert.Equal("MainWindow.xaml", step.Target);
        Assert.Equal(Generation, step.Generation);
        Assert.Equal("MainWindow.xaml", step.Arguments["path"]);
        Assert.Equal(Encoding.UTF8.GetString(Before), step.Arguments["contents"]);
        Assert.Equal("true", step.Arguments["overwrite"]);
    }

    [Fact]
    public void A_file_Boo_created_plans_a_delete()
    {
        var journal = new FileUndoJournal();
        journal.Record("notes/new.txt", previous: null, After);

        var plan = journal.Plan(Generation, () => Encoding.UTF8.GetBytes(After));

        Assert.True(plan.CanUndo);
        var step = Assert.IsType<ToolRequest>(plan.Step);
        Assert.Equal("delete_file", step.Name);
        Assert.Equal("notes/new.txt", step.Arguments["path"]);
        Assert.False(step.Arguments.ContainsKey("contents"));
    }

    [Fact]
    public void A_delete_plans_a_write_of_the_deleted_text_when_nothing_took_its_place()
    {
        var journal = new FileUndoJournal();
        journal.Record("old.txt", Before, written: null);

        var restored = journal.Plan(Generation, () => null);
        Assert.True(restored.CanUndo);
        Assert.Equal("write_file", restored.Step!.Name);
        Assert.Equal(Encoding.UTF8.GetString(Before), restored.Step.Arguments["contents"]);

        var replaced = journal.Plan(Generation, () => Encoding.UTF8.GetBytes("someone else"));
        Assert.False(replaced.CanUndo);
        Assert.Equal(FileUndoJournal.ChangedSinceEdit, replaced.BlockedReason);
    }

    [Fact]
    public void A_file_changed_after_the_edit_blocks_the_plan()
    {
        var journal = new FileUndoJournal();
        journal.Record("MainWindow.xaml", Before, After);

        var edited = journal.Plan(Generation, () => Encoding.UTF8.GetBytes(After + "<!-- user -->"));
        var gone = journal.Plan(Generation, () => null);

        Assert.False(edited.CanUndo);
        Assert.Equal(FileUndoJournal.ChangedSinceEdit, edited.BlockedReason);
        Assert.False(gone.CanUndo);
        Assert.Equal(FileUndoJournal.ChangedSinceEdit, gone.BlockedReason);
        Assert.True(journal.HasPendingUndo, "a blocked plan keeps the record; the user may restore the file and ask again");
    }

    [Fact]
    public void Binary_previous_content_is_recorded_but_not_restorable()
    {
        var journal = new FileUndoJournal();
        var binary = new byte[] { 0xFF, 0xFE, 0x00, 0xC3, 0x28 };
        journal.Record("icon.bin", binary, After);

        var plan = journal.Plan(Generation, () => Encoding.UTF8.GetBytes(After));

        Assert.True(journal.HasPendingUndo);
        Assert.False(plan.CanUndo);
        Assert.Equal(FileUndoJournal.NotText, plan.BlockedReason);
    }

    [Fact]
    public void Utf8_with_bom_round_trips_byte_for_byte()
    {
        var journal = new FileUndoJournal();
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("x = 1\r\n")).ToArray();
        journal.Record("a.py", withBom, After);

        var plan = journal.Plan(Generation, () => Encoding.UTF8.GetBytes(After));

        Assert.True(plan.CanUndo);
        Assert.Equal(withBom, Encoding.UTF8.GetBytes(plan.Step!.Arguments["contents"]));
    }

    [Fact]
    public void A_second_record_replaces_the_first_and_Clear_forgets()
    {
        var journal = new FileUndoJournal();
        journal.Record("a.txt", Before, "one");
        journal.Record("b.txt", Encoding.UTF8.GetBytes("one"), "two");

        Assert.Equal("b.txt", journal.RelativePath);
        var plan = journal.Plan(Generation, () => Encoding.UTF8.GetBytes("two"));
        Assert.Equal("one", plan.Step!.Arguments["contents"]);

        journal.Clear();
        Assert.False(journal.HasPendingUndo);
        Assert.Equal(FileUndoJournal.NothingRecorded, journal.Plan(Generation, () => null).BlockedReason);
    }
}
