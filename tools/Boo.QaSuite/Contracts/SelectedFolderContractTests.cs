using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Boo.Core;
using Boo.Providers;

namespace Boo.QaSuite.Contracts;

/// <summary>MOCK transport contract, never evidence that a real model follows the folder instructions.</summary>
public sealed class SelectedFolderContractTests
{
    [Theory]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, false, true)]
    public async Task C66_SelectedFolderContext_RequiresAnExplicitTaskAndConfiguredRoot(bool allowActions, bool selectedRoot, bool expected, bool withFrame)
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        var client = AssistantProviders.CreateScreenAssistant(AiProvider.OpenAI, http, () => "fixture-key", new ProviderSettings(), () => selectedRoot);
        var frame = withFrame ? new CapturedFrame(new ScreenContext(Guid.NewGuid(), 1,
            new DisplayInfo("fixture", "Fixture display", new RectD(0, 0, 100, 100), 1), 100, 100,
            new WindowIdentity(1, 2, "fixture", new RectD(0, 0, 100, 100)), DateTimeOffset.UtcNow), [1, 2, 3]) : null;
        var reply = await client.RespondAsync(new AssistantRequest("create note.txt in the task folder", 1, frame,
            AllowActions: allowActions), CancellationToken.None);
        Assert.Equal("ok", reply.Text);
        Assert.NotNull(handler.Body);
        using var body = JsonDocument.Parse(handler.Body!);
        var instructions = body.RootElement.GetProperty("instructions").GetString()!;
        var inputText = body.RootElement.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString()!;
        const string context = "File task context (authoritative application setting): the user has selected the task folder in Boo.";
        Assert.Equal(expected, instructions.Contains(context, StringComparison.Ordinal));
        Assert.Equal(expected, inputText.Contains(context, StringComparison.Ordinal));
        if (expected)
        {
            Assert.Contains("relative filename", instructions, StringComparison.Ordinal);
            Assert.Contains("Authorized relative file proposals are permitted without screen target evidence", instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("Do not invent observations, targets, coordinates, or actions.", instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("Marks must use image pixel coordinates and actions must refer only to this submitted target evidence.", instructions, StringComparison.Ordinal);
            Assert.Contains("screen/UI actions", instructions, StringComparison.Ordinal);
            Assert.Contains("requires approval before writing", instructions, StringComparison.Ordinal);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"output":[{"type":"message","content":[{"type":"output_text","text":"{\"text\":\"ok\",\"marks\":[],\"actions\":[]}"}]}]}
                    """, Encoding.UTF8, "application/json")
            };
        }
    }
}