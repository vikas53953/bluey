using System.Text;
using System.Text.Json;
using Boo.Core;
using static Boo.Providers.Tests.ProviderTestKit;

namespace Boo.Providers.Tests;

/// <summary>
/// Fuzzes the shared structured-reply decoder through the Gemini client (the thinnest wrapper around
/// StructuredReplies): random JSON that any provider could return must either become a bounded, catalog-only
/// reply or a classified failure, never an unhandled exception or an unbounded result.
/// </summary>
public sealed class StructuredReplyFuzzTests
{
    private const int Iterations = 600;
    private static readonly Uri Origin = new("https://generativelanguage.googleapis.com/");
    private static readonly ProviderSettings Settings = new(TextProvider: AiProvider.Gemini);

    private static string CandidateJson(string text) =>
        "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":" + JsonSerializer.Serialize(text) + "}]},\"finishReason\":\"STOP\"}]}";

    private static string Alphabet(Random random, int max)
    {
        string[] pieces = { "a", " ", "\n", "\0", "\u0001", "\uD83D\uDE00", "\uD83D", "{", "}", "\"", "\\", "é", "<b>", "javascript:" };
        var builder = new StringBuilder();
        for (var i = random.Next(0, max); i > 0; i--) builder.Append(pieces[random.Next(pieces.Length)]);
        return builder.ToString();
    }

    private static string Number(Random random) => random.Next(8) switch
    {
        0 => "1e400", 1 => "-1e400", 2 => "0", 3 => "-0.5", 4 => "99999999999999999999", 5 => "\"12\"", 6 => "null",
        _ => random.Next(-100, 5000).ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static string Scalar(Random random) => random.Next(6) switch
    {
        0 => "null", 1 => "true", 2 => "[]", 3 => "{}", 4 => Number(random), _ => JsonSerializer.Serialize(Alphabet(random, 10)),
    };

    private static string Mark(Random random)
    {
        var kind = random.Next(3) switch { 0 => "\"ring\"", 1 => "\"arrow\"", _ => random.Next(2) == 0 ? "\"label\"" : Scalar(random) };
        return "{\"kind\":" + kind + ",\"x\":" + Number(random) + ",\"y\":" + Number(random) + ",\"width\":" + Number(random) +
               ",\"height\":" + Number(random) + ",\"label\":" + Scalar(random) + ",\"endX\":" + Number(random) + ",\"endY\":" + Number(random) + "}";
    }

    private static readonly string[] Names = { "search", "inspect", "open_url", "open_app", "click", "type_text", "write_file", "delete_file", "run_shell", "CLICK", "" };

    private static string Action(Random random)
    {
        var name = random.Next(5) == 0 ? Scalar(random) : JsonSerializer.Serialize(Names[random.Next(Names.Length)]);
        var arguments = random.Next(4) == 0
            ? Scalar(random)
            : "{\"automationId\":" + Scalar(random) + ",\"name\":" + Scalar(random) + ",\"x\":" + Scalar(random) + ",\"y\":" + Scalar(random) +
              ",\"text\":" + Scalar(random) + ",\"path\":" + Scalar(random) + ",\"nested\":{\"deep\":[" + Scalar(random) + "]}}";
        return "{\"name\":" + name + ",\"target\":" + (random.Next(5) == 0 ? Scalar(random) : JsonSerializer.Serialize(Alphabet(random, 12))) +
               ",\"arguments\":" + arguments + ",\"generation\":" + Number(random) + "}";
    }

    private static string Reply(Random random)
    {
        if (random.Next(10) == 0) return Alphabet(random, 20);
        if (random.Next(10) == 0) return "{\"text\":\"ok\",\"marks\":[" + Mark(random); // truncated JSON
        var marks = random.Next(12) == 0 ? Scalar(random) : "[" + string.Join(",", Enumerable.Range(0, random.Next(0, random.Next(10) == 0 ? 400 : 6)).Select(_ => Mark(random))) + "]";
        var actions = random.Next(12) == 0 ? Scalar(random) : "[" + string.Join(",", Enumerable.Range(0, random.Next(0, random.Next(10) == 0 ? 300 : 5)).Select(_ => Action(random))) + "]";
        var text = random.Next(8) == 0 ? Scalar(random) : JsonSerializer.Serialize(random.Next(20) == 0 ? new string('x', 200_000) : Alphabet(random, 30));
        var parts = new List<string>();
        if (random.Next(10) != 0) parts.Add("\"text\":" + text);
        if (random.Next(10) != 0) parts.Add("\"marks\":" + marks);
        if (random.Next(10) != 0) parts.Add("\"actions\":" + actions);
        if (random.Next(3) == 0) parts.Add("\"observationId\":\"invented\"");
        return "{" + string.Join(",", parts) + "}";
    }

    [Fact]
    public async Task Random_structured_replies_become_bounded_replies_or_classified_failures()
    {
        foreach (var seed in new[] { 11, 23, 47, 89 })
        {
            var random = new Random(seed);
            for (var i = 0; i < Iterations; i++)
            {
                var context = CreateContext(Guid.NewGuid(), 41);
                var frame = new CapturedFrame(context, new byte[] { 1, 2, 3 });
                var controls = random.Next(2) == 0 ? BindingControls(context) : null;
                var modelText = Reply(random);
                var handler = new RecordingHandler(_ => JsonResponse(CandidateJson(modelText)));
                using var http = CreateHttpClient(handler, Origin);
                var client = new GeminiClient(http, () => "key", Settings);
                var request = new AssistantRequest("Explain.", 41, frame, AllowActions: random.Next(4) != 0);

                AssistantReply reply;
                try
                {
                    reply = await client.RespondAsync(request, CancellationToken.None, controls);
                }
                catch (InvalidOperationException)
                {
                    continue; // ProviderException or a plain classified failure: the app shows a safe message.
                }
                catch (Exception ex)
                {
                    throw new Xunit.Sdk.XunitException($"seed={seed} i={i} {ex.GetType().Name}: {ex.Message}\n{modelText}");
                }

                Assert.False(string.IsNullOrWhiteSpace(reply.Text));
                Assert.InRange(reply.Text.Length, 1, StructuredReplies.MaxTextLength);
                Assert.InRange(reply.Actions.Count, 0, StructuredReplies.MaxActions);
                foreach (var action in reply.Actions)
                {
                    Assert.Contains(action.Name, ActionPolicy.ToolNames);
                    Assert.Equal(41, action.Generation);
                    Assert.InRange(action.Target.Length, 0, StructuredReplies.MaxFieldLength);
                    Assert.All(action.Arguments.Values, v => Assert.InRange(v.Length, 0, StructuredReplies.MaxArgumentLength));
                    if (controls is not null && action.Name == "type_text")
                        Assert.Equal("edit-field", action.Arguments["automationId"]);
                    if (controls is not null && action.Name == "click" && action.Arguments.ContainsKey("automationId"))
                        Assert.Equal("Button text", action.Arguments["name"]);
                }
                if (reply.Annotations is { } proposal)
                {
                    Assert.Equal(context.ObservationId, proposal.ObservationId);
                    Assert.Equal(41, proposal.Generation);
                    Assert.InRange(proposal.Marks.Count, 1, StructuredReplies.MaxMarks);
                    foreach (var mark in proposal.Marks)
                    {
                        Assert.Contains(mark.Kind, new[] { "ring", "arrow", "label" });
                        Assert.InRange(mark.Label.Length, 0, StructuredReplies.MaxFieldLength);
                        Assert.True(double.IsFinite(mark.Bounds.X) && double.IsFinite(mark.Bounds.Width));
                    }
                }
            }
        }
    }

    [Fact]
    public async Task Random_nudge_replies_are_bounded_or_fail_safely()
    {
        foreach (var seed in new[] { 5, 17 })
        {
            var random = new Random(seed);
            for (var i = 0; i < Iterations; i++)
            {
                var context = CreateContext(Guid.NewGuid(), 3);
                var json = random.Next(6) == 0 ? Alphabet(random, 20) : "{\"text\":" + (random.Next(4) == 0 ? Scalar(random) : JsonSerializer.Serialize(Alphabet(random, 400))) +
                    ",\"pointer_target\":" + (random.Next(3) == 0 ? Scalar(random) : JsonSerializer.Serialize(Alphabet(random, 200))) + "}";
                var handler = new RecordingHandler(_ => JsonResponse(CandidateJson(json)));
                using var http = CreateHttpClient(handler, Origin);
                var client = new GeminiClient(http, () => "key", Settings);
                FocusNudge nudge;
                try { nudge = await client.NudgeAsync(new CapturedFrame(context, new byte[] { 1 }), "task", CancellationToken.None); }
                catch (InvalidOperationException) { continue; }
                catch (Exception ex) { throw new Xunit.Sdk.XunitException($"seed={seed} i={i} {ex.GetType().Name}: {ex.Message}\n{json}"); }
                Assert.InRange(nudge.Text.Length, 0, 160);
                if (nudge.PointerTarget is { } target) Assert.InRange(target.Length, 1, 80);
            }
        }
    }
}
