using System.Text;

namespace Boo.Core;

public sealed record FileUndoPlan(bool CanUndo, string? BlockedReason, ToolRequest? Step);

/// <summary>
/// Remembers the last file edit Boo committed (<c>write_file</c> or <c>delete_file</c>) and plans
/// the single reverse step: rewrite the previous UTF-8 text, or delete a file Boo created.
/// It never guesses: a file that changed since Boo's edit, or a previous content that was not
/// UTF-8 text, blocks the plan with a plain reason. The reverse step goes through the normal
/// approval queue like any other action.
/// </summary>
public sealed class FileUndoJournal
{
    public const string NothingRecorded = "Nothing to undo: Boo has no recorded file edit.";
    public const string ChangedSinceEdit = "The file changed after Boo's edit, so Boo will not overwrite it.";
    public const string NotText = "The file's previous content was not UTF-8 text; Boo cannot restore it.";

    private FileEdit? _edit;

    public bool HasPendingUndo => _edit is not null;

    /// <summary>The root-relative path of the recorded edit, for status text.</summary>
    public string? RelativePath => _edit?.RelativePath;

    /// <summary>
    /// Records a committed, verified file edit. <paramref name="previous"/> is the file's bytes before
    /// the edit (null when it did not exist); <paramref name="written"/> is the text Boo wrote (null for a delete).
    /// </summary>
    public void Record(string relativePath, byte[]? previous, string? written)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (previous is null && written is null)
        {
            throw new ArgumentException("A delete of a file that did not exist cannot be recorded.", nameof(written));
        }

        string? previousText = null;
        var restorable = true;
        if (previous is not null)
        {
            if (previous.Length > FileActionResolver.MaximumWriteBytes)
            {
                restorable = false;
            }
            else
            {
                previousText = Encoding.UTF8.GetString(previous);
                restorable = Encoding.UTF8.GetBytes(previousText).AsSpan().SequenceEqual(previous);
                if (!restorable)
                {
                    previousText = null;
                }
            }
        }

        _edit = new FileEdit(relativePath, previous is not null, previousText, restorable, written);
    }

    public void Clear() => _edit = null;

    /// <summary>
    /// Plans the reverse step. <paramref name="readCurrent"/> returns the file's current bytes, or null
    /// when it does not exist; it is Boo's only view of the file, so the plan is conservative.
    /// </summary>
    public FileUndoPlan Plan(long generation, Func<byte[]?> readCurrent)
    {
        ArgumentNullException.ThrowIfNull(readCurrent);
        if (_edit is not { } edit)
        {
            return Blocked(NothingRecorded);
        }

        var current = readCurrent();
        if (edit.Written is null)
        {
            if (current is not null)
            {
                return Blocked(ChangedSinceEdit);
            }
        }
        else if (current is null || !current.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(edit.Written)))
        {
            return Blocked(ChangedSinceEdit);
        }

        if (!edit.Existed)
        {
            return new FileUndoPlan(true, null, new ToolRequest("delete_file", edit.RelativePath,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["path"] = edit.RelativePath }, generation));
        }

        if (!edit.Restorable || edit.PreviousText is null)
        {
            return Blocked(NotText);
        }

        return new FileUndoPlan(true, null, new ToolRequest("write_file", edit.RelativePath,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["path"] = edit.RelativePath,
                ["contents"] = edit.PreviousText,
                ["overwrite"] = "true",
            }, generation));
    }

    private static FileUndoPlan Blocked(string reason) => new(false, reason, null);

    private sealed record FileEdit(string RelativePath, bool Existed, string? PreviousText, bool Restorable, string? Written);
}
