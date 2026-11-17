using Boo.Core;
using static Boo.Providers.Tests.ProviderTestKit;

namespace Boo.Providers.Tests;

/// <summary>
/// Issue #181 (j15e): a plain question (no explicit task, AllowActions false) whose structured answer still carries an
/// action must report that action to the app, validated and inert, so the app can show the required acknowledgment
/// "Boo suggested an action, but no explicit task request authorized it.". Before the fix the parser dropped it, so the
/// app could only say "Reply complete. No action was committed.". Reporting it grants nothing: only an explicit task
/// may offer an action for approval (RenderReply), which this test does not change.
/// </summary>
public sealed class IncidentalActionTests
{
    private static readonly ProviderSettings Settings = new(ReasoningModel: "gpt-6.1-sol");
    private static readonly Uri Origin = new("https://api.openai.com/");

    private const string IncidentalWriteJson =
        "{\"text\":\"Editing means changing a file.\",\"marks\":[],\"actions\":[{\"name\":\"write_file\",\"target\":\"MainWindow.xaml\",\"arguments\":{\"path\":\"MainWindow.xaml\",\"content\":\"QA-EDIT\"},\"generation\":999}]}";

    private static string ResponsesJson(string structuredText) =>
        "{\"id\":\"resp_1\",\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":" +
        System.Text.Json.JsonSerializer.Serialize(structuredText) + "}]}]}";

    [Fact]
    public async Task A_plain_question_reports_an_incidental_action_so_the_app_can_acknowledge_it()
    {
        var handler = new RecordingHandler(_ => JsonResponse(ResponsesJson(IncidentalWriteJson)));
        using var http = CreateHttpClient(handler, Origin);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);
        var request = new AssistantRequest("What does edit mean?", 7, Frame: null, Research: false, AllowActions: false);

        var reply = await client.RespondAsync(request, CancellationToken.None);

        Assert.Equal("Editing means changing a file.", reply.Text);
        var action = Assert.Single(reply.Actions);
        Assert.Equal("write_file", action.Name);
        Assert.Equal("MainWindow.xaml", action.Target);
        Assert.Equal(request.Generation, action.Generation);   // stamped by Boo, never the model's 999
    }

    [Fact]
    public async Task A_plain_question_with_an_action_outside_the_catalog_fails_closed_like_a_task_does()
    {
        var handler = new RecordingHandler(_ => JsonResponse(ResponsesJson(OutsideCatalogJson)));
        using var http = CreateHttpClient(handler, Origin);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        var error = await AssertNotStubAsync(() => client.RespondAsync(
            new AssistantRequest("What is a shell?", 8, Frame: null, Research: false, AllowActions: false), CancellationToken.None));

        Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.DoesNotContain("rm -rf", error.Message, StringComparison.Ordinal);
    }
}
