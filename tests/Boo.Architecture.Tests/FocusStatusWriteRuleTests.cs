using System.IO;
using System.Text.RegularExpressions;

namespace Boo.Architecture.Tests;

/// <summary>
/// PR #170 (issue #168): every write to the focus status goes through MainWindow.SetFocusStatus (or the acknowledgment
/// code beside it in MainWindow.FocusHistory.cs), so each write makes older pending "saved" acknowledgments stale, even
/// when it repeats the text already shown. A direct "FocusStatus.Text = ..." anywhere else in Boo.App would bypass that,
/// so it is a failure here.
/// </summary>
public sealed class FocusStatusWriteRuleTests
{
    private static readonly Regex DirectWrite = new(@"\bFocusStatus\.Text\s*=(?!=)", RegexOptions.Compiled);

    [Fact]
    public void Focus_status_is_written_only_through_SetFocusStatus()
    {
        var appDirectory = Path.Combine(RepoRoot(), "src", "Boo.App");
        var offenders = Directory.GetFiles(appDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !string.Equals(Path.GetFileName(file), "MainWindow.FocusHistory.cs", StringComparison.Ordinal))
            .SelectMany(file => File.ReadAllLines(file)
                .Select((line, index) => (line, index))
                .Where(entry => DirectWrite.IsMatch(entry.line))
                .Select(entry => $"{Path.GetFileName(file)}:{entry.index + 1}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Write the focus status with SetFocusStatus(...), not FocusStatus.Text = ...: " + string.Join(", ", offenders));
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Boo.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Boo.sln not found above " + AppContext.BaseDirectory);
    }
}
