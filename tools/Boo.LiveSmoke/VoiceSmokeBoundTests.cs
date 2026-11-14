using System.Reflection;
using System.Text.Json;
using Boo.Core;
using Boo.Providers;

namespace Boo.LiveSmoke;

/// <summary>
/// Offline checks (no key, no socket, no paid call) that the live voice smoke and the daily sentinel can spend at most
/// ONE paid voice response (issue #129). The smoke must build its client through LiveVoiceRoundTripTests.CreateClient,
/// in Boo's existing bounded realtime mode: output capped at MaxOutputTokens, no tools offered (so the model cannot
/// start a tool round that asks for a second response), and a second response request refused before anything is sent.
/// The app's normal voice session is not bounded and is not changed.
/// </summary>
public sealed class VoiceSmokeBoundTests
{
    private static RealtimeClient SmokeClient() =>
        LiveVoiceRoundTripTests.CreateClient(() => null, new ProviderSettings(TextProvider: AiProvider.OpenAI), "gpt-4o-mini-transcribe");

    [Fact]
    public async Task The_voice_smoke_session_caps_its_output_and_offers_no_tools()
    {
        await using var client = SmokeClient();
        using var json = JsonDocument.Parse(client.CreateSessionUpdate());
        var session = json.RootElement.GetProperty("session");
        Assert.True(session.TryGetProperty("max_output_tokens", out var cap),
            "the voice smoke session must cap its output (max_output_tokens); without it one turn has no spend bound");
        Assert.Equal(LiveVoiceRoundTripTests.MaxOutputTokens, cap.GetInt32());
        Assert.Equal("none", session.GetProperty("tool_choice").GetString());
    }

    [Fact]
    public async Task The_voice_smoke_client_refuses_a_second_paid_response()
    {
        var client = SmokeClient();
        var request = typeof(RealtimeClient).GetMethod("RequestResponseAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var first = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await (Task)request.Invoke(client, new object[] { CancellationToken.None })!);
        Assert.Equal("Realtime connection is not open.", first.Message);
        var second = Assert.Throws<TargetInvocationException>(() => request.Invoke(client, new object[] { CancellationToken.None }));
        Assert.Contains("exactly one response", second.InnerException!.Message);
        await client.DisposeAsync();
    }
}
