using System.Reflection;
using System.Text.Json;
using Boo.Core;
using Boo.Providers;

namespace Boo.Integration.Tests;

/// <summary>
/// Issue #179: Boo's voice session tells the model, for every answer that rests on numbers, quantities, dates or facts, to
/// work it out before speaking and to say the numbers and the operation with the result. It is one rule for the whole class
/// of factual and numeric answers (not a per-question patch), and it is in every voice session Boo opens: the normal one,
/// push-to-talk, and the bounded QA session.
/// </summary>
public sealed class VoiceCarefulAnswerTests
{
    private static string Instructions(string sessionUpdateJson)
    {
        using var document = JsonDocument.Parse(sessionUpdateJson);
        return document.RootElement.GetProperty("session").GetProperty("instructions").GetString()!;
    }

    private static string CarefulRule() =>
        (string)(typeof(RealtimeProtocol).GetField("CarefulAnswerInstruction", BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue()
                 ?? throw new MissingFieldException(nameof(RealtimeProtocol), "CarefulAnswerInstruction"));

    [Fact]
    public void Every_voice_session_asks_for_careful_numeric_and_factual_answers()
    {
        var rule = CarefulRule();
        Assert.Contains("numbers, quantities, dates, or facts", rule, StringComparison.Ordinal);
        Assert.Contains("work it out carefully before you speak", rule, StringComparison.Ordinal);
        Assert.Contains("say the numbers and the operation together with the result", rule, StringComparison.Ordinal);
        Assert.Contains("Never guess a number", rule, StringComparison.Ordinal);

        var settings = new ProviderSettings();
        foreach (var session in new[]
                 {
                     RealtimeProtocol.SessionUpdate(settings),
                     RealtimeProtocol.SessionUpdate(settings, pushToTalk: true),
                     RealtimeProtocol.SessionUpdate(settings, pushToTalk: true, maxOutputTokens: 128),
                 })
        {
            Assert.Contains(rule, Instructions(session), StringComparison.Ordinal);
        }
    }
}
