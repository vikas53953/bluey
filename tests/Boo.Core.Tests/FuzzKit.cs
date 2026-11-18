using System.Text;

namespace Boo.Core.Tests;

/// <summary>
/// A small deterministic generator for property-style fuzz tests. Every test seeds its own <see cref="Random"/>
/// so a failure reproduces from the seed printed in the assertion message; no external package is needed.
/// </summary>
internal sealed class FuzzKit
{
    public const int Iterations = 2_000;

    public FuzzKit(int seed) { Seed = seed; Random = new Random(seed); }

    public int Seed { get; }
    public Random Random { get; }

    private static readonly string[] Alphabet =
    {
        "a", "Z", "0", " ", "\t", "\n", "\r", "\0", "\u0001", "\u007F", "\u0085", "\u00A0", "\u2028", "\u200B", "\uFEFF",
        "\uD83D\uDE00", "\uD83D", "\uDE00", "é", "ß", "İ", "中", "\\", "/", ":", "..", ".", "\"", "'", "{", "}", "[", "]",
        "<", ">", "&", "=", "?", "#", "@", "%", "%00", "%2e%2e", "http://", "https://", "javascript:", "file:",
        "\\\\?\\", "C:", "evil.test", "bank.example", "utm_source", "1e308", "-0", "NaN", "null", "true",
    };

    public string Text(int maxLength = 64)
    {
        var builder = new StringBuilder();
        var pieces = Random.Next(0, maxLength);
        for (var i = 0; i < pieces; i++)
            builder.Append(Alphabet[Random.Next(Alphabet.Length)]);
        return builder.ToString();
    }

    public string? MaybeText(int maxLength = 64) => Random.Next(8) == 0 ? null : Text(maxLength);

    public double Double()
    {
        switch (Random.Next(12))
        {
            case 0: return double.NaN;
            case 1: return double.PositiveInfinity;
            case 2: return double.NegativeInfinity;
            case 3: return double.MaxValue;
            case 4: return double.MinValue;
            case 5: return double.Epsilon;
            case 6: return -0.0;
            case 7: return 0;
            case 8: return Random.Next(-10, 5000);
            default: return (Random.NextDouble() - 0.5) * Math.Pow(10, Random.Next(-5, 12));
        }
    }

    public long Long() => Random.Next(6) switch
    {
        0 => long.MinValue, 1 => long.MaxValue, 2 => -1, 3 => 0, _ => Random.NextInt64(-5, 100),
    };

    /// <summary>A JSON literal: scalars, strings, nested arrays and objects up to a small depth.</summary>
    public string Json(int depth = 0)
    {
        var choice = Random.Next(depth > 3 ? 7 : 10);
        switch (choice)
        {
            case 0: return "null";
            case 1: return "true";
            case 2: return "false";
            case 3: return Random.Next(5) == 0 ? "1e400" : Double() is var d && double.IsFinite(d) ? d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "-0";
            case 4: return Random.Next(3) == 0 ? "\"" + Text(8) + "\"" : System.Text.Json.JsonSerializer.Serialize(Text(12));
            case 5: return Random.NextInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
            case 6: return "123456789012345678901234567890";
            case 7:
                return "[" + string.Join(",", Enumerable.Range(0, Random.Next(0, 4)).Select(_ => Json(depth + 1))) + "]";
            default:
                return "{" + string.Join(",", Enumerable.Range(0, Random.Next(0, 5)).Select(_ =>
                    System.Text.Json.JsonSerializer.Serialize(Key()) + ":" + Json(depth + 1))) + "}";
        }
    }

    public string Key() => Random.Next(3) == 0 ? Text(6) : Keys[Random.Next(Keys.Length)];

    private static readonly string[] Keys =
    {
        "text", "marks", "actions", "kind", "x", "y", "width", "height", "label", "endX", "endY", "name", "target",
        "arguments", "automationId", "path", "content", "overwrite", "task", "minutes", "pointer_target", "url",
        "title", "type", "output", "annotations", "sources", "action",
    };

    public T Pick<T>(IReadOnlyList<T> items) => items[Random.Next(items.Count)];

    public string Describe(string input) => $"seed={Seed} input={System.Text.Json.JsonSerializer.Serialize(input)}";
}
