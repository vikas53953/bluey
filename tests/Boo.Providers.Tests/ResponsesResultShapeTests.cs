using System.Net;
using Boo.Core;
using static Boo.Providers.Tests.ProviderTestKit;

namespace Boo.Providers.Tests;

/// <summary>
/// Review regression: a Responses result whose root or "output" has the wrong JSON kind must fail with the vendor's
/// own "invalid result" wording, not with <c>System.Text.Json</c>'s element-kind message, which the app would show.
/// </summary>
public sealed class ResponsesResultShapeTests
{
    private static readonly ProviderSettings Settings = new(ReasoningModel: "gpt-6.1-sol");
    private static readonly Uri Origin = new("https://api.openai.com/");

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{\"output\":{}}")]
    [InlineData("{\"output\":\"done\"}")]
    [InlineData("{\"id\":\"resp_1\"}")]
    public void DecodeNudge_reports_wrong_shapes_with_the_vendor_message(string json)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ResponsesClient.DecodeNudge(json));

        Assert.Equal("OpenAI nudge returned an invalid result.", exception.Message);
    }

    [Fact]
    public void DecodeNudge_skips_output_items_and_parts_of_the_wrong_kind()
    {
        const string json = "{\"output\":[null,7,{\"content\":[\"x\",{\"type\":5},{\"type\":\"output_text\",\"text\":\"{\\\"text\\\":\\\"Back to the report?\\\",\\\"pointer_target\\\":null}\"}]}]}";

        var nudge = ResponsesClient.DecodeNudge(json);

        Assert.Equal("Back to the report?", nudge.Text);
        Assert.Null(nudge.PointerTarget);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"output\":{}}")]
    public async Task RespondAsync_reports_wrong_shapes_with_the_vendor_message(string json)
    {
        var handler = new RecordingHandler(_ => JsonResponse(json, HttpStatusCode.OK));
        var client = new ResponsesClient(CreateHttpClient(handler, Origin), () => "sk-test", Settings);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RespondAsync(new AssistantRequest("What is Boo?", 3, Research: true), CancellationToken.None));

        Assert.Equal("OpenAI responses returned an invalid result.", exception.Message);
        Assert.Single(handler.Requests);
    }
}
