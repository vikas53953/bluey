using System.Text.Json;
using Boo.Core;
using Boo.Providers;
using Boo.Windows;
using NAudio.Wave;

namespace Boo.NativeVoice.Unit;
public sealed class AudioBoundaryTests
{
    [Fact]
    public async Task Play_IsConsumedByVirtualDevice_AndCaptureEmitsEntireWav()
    {
        var root = Path.Combine(Path.GetTempPath(), "boo-audio-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var wav = Path.Combine(root, "input.wav");
            var receipt = Path.Combine(root, "device.json");
            var pcm = Enumerable.Range(0, 9600).Select(n => (byte)(n % 251 + 1)).ToArray();
            using (var writer = new WaveFileWriter(wav, new WaveFormat(24000, 16, 1))) writer.Write(pcm);
            using var device = new HostedVoiceAudio(wav, receipt);
            using var session = new AudioSession(device);
            var captured = new List<byte>();
            session.Captured += b => { lock(captured) captured.AddRange(b); };
            Assert.True(session.TryStart(-1, out _));
            session.Play(pcm);
            var complete = false;
            for (var i = 0; i < 100; i++)
            {
                await Task.Delay(20);
                if (!File.Exists(receipt)) continue;
                using var stream = new FileStream(receipt, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var lines = reader.ReadToEnd();
                var completeLine = lines.LastIndexOf('\n');
                if (completeLine < 0) continue;
                using var json = JsonDocument.Parse(lines[..completeLine].Split('\n')[^1]);
                if (json.RootElement.GetProperty("inputBytes").GetInt64() == pcm.Length
                    && json.RootElement.GetProperty("renderedBytes").GetInt64() == pcm.Length)
                { complete = true; break; }
            }
            Assert.True(complete, "Virtual output must consume every Play byte; capture must emit every WAV byte.");
            lock(captured) Assert.Equal(pcm, captured.ToArray());
            Assert.Throws<InvalidOperationException>(() => session.TryStart(-1, out _));
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(false, "true", "github-hosted")]
    [InlineData(true, "false", "github-hosted")]
    [InlineData(true, "true", "self-hosted")]
    public void OptInFailsBeforeDeviceAccessOutsideHostedRunner(bool windows, string actions, string runner)
    {
        string? Read(string name) => name switch
        {
            "BOO_QA_VOICE_INPUT" => "not-opened.wav", "GITHUB_ACTIONS" => actions,
            "RUNNER_ENVIRONMENT" => runner, "BOO_QA_VOICE_EVIDENCE" => "receipt.jsonl", _ => null
        };
        var error = Assert.Throws<InvalidOperationException>(() => HostedVoiceOptions.Read(Read, windows));
        Assert.Equal("QA voice devices require GitHub-hosted Windows.", error.Message);
    }
    [Fact]
    public async Task NativeFactoryForwardsModelTranscriptionAndBudgetToActualSessionPayload()
    {
        await using var client = RealtimeClient.ForNativeVoice(() => null,
            new ProviderSettings(RealtimeModel: "model-override"), true,
            new HostedVoiceOptions("input.wav", "receipt", "transcription-override"));
        using var json = JsonDocument.Parse(client.CreateSessionUpdate());
        var session = json.RootElement.GetProperty("session");
        Assert.Equal("model-override", session.GetProperty("model").GetString());
        Assert.Equal("transcription-override", session.GetProperty("audio").GetProperty("input").GetProperty("transcription").GetProperty("model").GetString());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("audio").GetProperty("input").GetProperty("turn_detection").ValueKind);
        Assert.Equal(128, session.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("none", session.GetProperty("tool_choice").GetString());
    }
    [Fact]
    public async Task BoundedClientRejectsSecondResponseWithoutOpeningSocket()
    {
        var client = new RealtimeClient(() => null, new ProviderSettings(), true, maxOutputTokens: 128);
        var request = typeof(RealtimeClient).GetMethod("RequestResponseAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var first = await Assert.ThrowsAsync<InvalidOperationException>(async () => await (Task)request.Invoke(client, new object[] { CancellationToken.None })!);
        Assert.Equal("Realtime connection is not open.", first.Message);
        var second = Assert.Throws<System.Reflection.TargetInvocationException>(() => request.Invoke(client, new object[] { CancellationToken.None }));
        Assert.Contains("exactly one response", second.InnerException!.Message);
        await client.DisposeAsync();
    }
    [Fact]
    public void BoundedSessionCapsTokensAndDisablesToolFollowups_ProductionUnchanged()
    {
        using var bounded = JsonDocument.Parse(RealtimeProtocol.SessionUpdate(new ProviderSettings(), true,
            "gpt-4o-mini-transcribe", 128));
        var s = bounded.RootElement.GetProperty("session");
        Assert.Equal(128, s.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("none", s.GetProperty("tool_choice").GetString());
        using var normal = JsonDocument.Parse(RealtimeProtocol.SessionUpdate(new ProviderSettings()));
        Assert.False(normal.RootElement.GetProperty("session").TryGetProperty("max_output_tokens", out _));
        Assert.Equal("auto", normal.RootElement.GetProperty("session").GetProperty("tool_choice").GetString());
    }
    [Fact]
    public void ToolTurnOptIn_ReadsExactlyOne_AndDefaultsOff()
    {
        string? Read(string key, string? toolTurn) => key switch
        {
            "BOO_QA_VOICE_INPUT" => "input.wav",
            "GITHUB_ACTIONS" => "true",
            "RUNNER_ENVIRONMENT" => "github-hosted",
            "BOO_QA_VOICE_EVIDENCE" => "receipt",
            HostedVoiceOptions.ToolTurnVariable => toolTurn,
            _ => null
        };
        Assert.False(HostedVoiceOptions.Read(k => Read(k, null), true)!.ToolTurn);
        Assert.False(HostedVoiceOptions.Read(k => Read(k, "true"), true)!.ToolTurn);
        Assert.True(HostedVoiceOptions.Read(k => Read(k, "1"), true)!.ToolTurn);
    }
    [Fact]
    public async Task ToolTurnSession_ForcesScreenHelp_KeepsTokenCap_ProductionUnchanged()
    {
        await using var client = RealtimeClient.ForNativeVoice(() => null, new ProviderSettings(), false,
            new HostedVoiceOptions("input.wav", "receipt", "gpt-4o-mini-transcribe", ToolTurn: true));
        using var json = JsonDocument.Parse(client.CreateSessionUpdate());
        var session = json.RootElement.GetProperty("session");
        Assert.Equal(128, session.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("function", session.GetProperty("tool_choice").GetProperty("type").GetString());
        Assert.Equal("screen_help", session.GetProperty("tool_choice").GetProperty("name").GetString());
        using var normal = JsonDocument.Parse(RealtimeProtocol.SessionUpdate(new ProviderSettings(), toolTurn: true));
        Assert.False(normal.RootElement.GetProperty("session").TryGetProperty("max_output_tokens", out _));
        Assert.Equal("auto", normal.RootElement.GetProperty("session").GetProperty("tool_choice").GetString());
    }
    [Fact]
    public async Task ToolTurnClientPermitsExactlyTwoResponses_WithoutOpeningSocket()
    {
        var client = new RealtimeClient(() => null, new ProviderSettings(), false, maxOutputTokens: 128, toolTurn: true);
        var request = typeof(RealtimeClient).GetMethod("RequestResponseAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        for (var i = 0; i < 2; i++)
        {
            var closed = await Assert.ThrowsAsync<InvalidOperationException>(async () => await (Task)request.Invoke(client, new object[] { CancellationToken.None })!);
            Assert.Equal("Realtime connection is not open.", closed.Message);
        }
        var third = Assert.Throws<System.Reflection.TargetInvocationException>(() => request.Invoke(client, new object[] { CancellationToken.None }));
        Assert.Contains("exactly two response", third.InnerException!.Message);
        await client.DisposeAsync();
    }
}
