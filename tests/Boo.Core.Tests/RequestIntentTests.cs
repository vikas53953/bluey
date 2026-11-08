using Boo.Core;

namespace Boo.Core.Tests;

public sealed class RequestIntentTests
{
    [Fact]
    public void Null_text_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => RequestIntent.Classify(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n  ")]
    public void Empty_or_whitespace_text_is_explain(string text)
    {
        Assert.Equal(RequestKind.Explain, RequestIntent.Classify(text));
    }

    [Theory]
    [InlineData("undo")]
    [InlineData("undo that")]
    [InlineData("undo it")]
    [InlineData("undo the edit")]
    [InlineData("undo the last edit")]
    [InlineData("undo last edit")]
    [InlineData("undo the change")]
    [InlineData("revert")]
    [InlineData("revert that")]
    [InlineData("revert it")]
    [InlineData("revert the edit")]
    [InlineData("put it back")]
    [InlineData("change it back")]
    public void Every_listed_undo_phrase_is_undo(string text)
    {
        Assert.Equal(RequestKind.Undo, RequestIntent.Classify(text));
    }

    [Theory]
    [InlineData("UNDO")]
    [InlineData("Undo That")]
    [InlineData("  undo it  ")]
    [InlineData("undo.")]
    [InlineData("undo!")]
    [InlineData("Undo that!!")]
    [InlineData("undo it.!.")]
    [InlineData("please undo")]
    [InlineData("Please undo the last edit.")]
    [InlineData("PLEASE REVERT IT!")]
    [InlineData("please   undo")]
    [InlineData("\tPut it back!\n")]
    [InlineData("please change it back.")]
    public void Undo_phrases_are_normalised_for_case_whitespace_trailing_punctuation_and_please(string text)
    {
        Assert.Equal(RequestKind.Undo, RequestIntent.Classify(text));
    }

    [Theory]
    [InlineData("undo?")]
    [InlineData("undo everything you did yesterday")]
    [InlineData("please please undo")]
    [InlineData("undoing is hard")]
    [InlineData("why did you undo that")]
    [InlineData("revert the database")]
    public void Text_that_is_not_exactly_an_undo_phrase_is_not_undo(string text)
    {
        Assert.NotEqual(RequestKind.Undo, RequestIntent.Classify(text));
    }

    [Fact]
    public void Undo_is_checked_before_task_prefixes()
    {
        // "change it back" also begins with the task prefix "change ".
        Assert.Equal(RequestKind.Undo, RequestIntent.Classify("change it back"));
    }

    [Theory]
    [InlineData("task rename the file")]
    [InlineData("please open the settings")]
    [InlineData("open Notepad")]
    [InlineData("please create a folder")]
    [InlineData("create a new note")]
    [InlineData("type hello world")]
    [InlineData("click the Apply edit button")]
    [InlineData("edit the button text")]
    [InlineData("please edit the title")]
    [InlineData("change the fill to green")]
    [InlineData("please change the label")]
    [InlineData("set the colour to red")]
    [InlineData("please set the text to Sign up")]
    [InlineData("apply the edit")]
    public void Every_listed_task_prefix_is_task(string text)
    {
        Assert.Equal(RequestKind.Task, RequestIntent.Classify(text));
    }

    [Theory]
    [InlineData("CLICK Save")]
    [InlineData("Open the browser")]
    [InlineData("   type hello")]
    [InlineData("Please Change the button text to Sign up")]
    public void Task_prefixes_are_matched_case_insensitively_after_trimming(string text)
    {
        Assert.Equal(RequestKind.Task, RequestIntent.Classify(text));
    }

    [Theory]
    [InlineData("open")]
    [InlineData("opener of this window")]
    [InlineData("clicker game rules")]
    [InlineData("settings look odd here")]
    [InlineData("what does type mean here")]
    public void Task_prefixes_require_their_trailing_space_and_a_leading_position(string text)
    {
        Assert.Equal(RequestKind.Explain, RequestIntent.Classify(text));
    }

    [Theory]
    [InlineData("find a good cafe nearby")]
    [InlineData("search the .NET 10 release notes")]
    [InlineData("research local LLM runtimes")]
    [InlineData("look up the weather in Pune")]
    [InlineData("lookup the WinUI 3 docs")]
    [InlineData("latest news on Windows")]
    [InlineData("what's the latest on .NET")]
    [InlineData("what is the latest version of xunit")]
    [InlineData("compare WPF and WinUI")]
    public void Every_listed_research_prefix_is_research(string text)
    {
        Assert.Equal(RequestKind.Research, RequestIntent.Classify(text));
    }

    [Theory]
    [InlineData("tell me about Diwali with sources")]
    [InlineData("explain this error WITH LINKS please")]
    [InlineData("summarise UI Automation and cite sources")]
    [InlineData("give me source links for that")]
    [InlineData("With Sources: how do I enable dark mode")]
    public void Research_markers_anywhere_in_the_text_are_research(string text)
    {
        Assert.Equal(RequestKind.Research, RequestIntent.Classify(text));
    }

    [Theory]
    [InlineData("FIND my keys")]
    [InlineData("  Compare these two")]
    [InlineData("What Is The Latest build")]
    public void Research_prefixes_are_matched_case_insensitively_after_trimming(string text)
    {
        Assert.Equal(RequestKind.Research, RequestIntent.Classify(text));
    }

    [Theory]
    [InlineData("open the latest release notes with sources")]
    [InlineData("change the button text with links")]
    [InlineData("click compare")]
    public void Task_is_checked_before_research(string text)
    {
        Assert.Equal(RequestKind.Task, RequestIntent.Classify(text));
    }

    [Theory]
    [InlineData("undo with sources")]
    [InlineData("revert that with links")]
    public void Undo_lookalikes_with_research_markers_are_research(string text)
    {
        Assert.Equal(RequestKind.Research, RequestIntent.Classify(text));
    }

    [Theory]
    [InlineData("what is this button")]
    [InlineData("finder window explained")]
    [InlineData("searching for meaning")]
    [InlineData("comparelist")]
    [InlineData("why is the latest build slow")]
    [InlineData("explain the selected control")]
    public void Everything_else_is_explain(string text)
    {
        Assert.Equal(RequestKind.Explain, RequestIntent.Classify(text));
    }
}
