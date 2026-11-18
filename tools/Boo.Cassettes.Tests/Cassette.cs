using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Boo.Cassettes;

/// <summary>
/// One recorded (or hand-made) OpenAI HTTP exchange, replayed by <see cref="CassetteHandler"/> (rule BOO-CASS-001,
/// docs/quality/cassettes.md). A cassette holds only what is safe to commit: a summary of the request shape, the reply
/// status, an allowlist of reply headers and the reply body as timed chunks. Never a key, an auth header or an
/// account id. Shared with the recorder (tools/Boo.Cassettes.Record), which links this file.
/// </summary>
public sealed class Cassette
{
    public int Schema { get; set; } = 1;
    public string Name { get; set; } = "";
    /// <summary>"recorded" (captured from the real API by record-cassettes.yml) or "synthetic" (hand-made).</summary>
    public string Origin { get; set; } = "synthetic";
    public string Note { get; set; } = "";
    /// <summary>Which client call replays it: "respond", "research" (prose with sources) or "nudge".</summary>
    public string Call { get; set; } = "respond";
    public CassetteRequest Request { get; set; } = new();
    public CassetteResponse? Response { get; set; }
    /// <summary>Fault injected by the replay instead of (or part-way through) the response.</summary>
    public CassetteFault? Fault { get; set; }
    public CassetteExpect Expect { get; set; } = new();
}

public sealed class CassetteRequest
{
    public string Method { get; set; } = "POST";
    public string Path { get; set; } = "/v1/responses";
    public string Model { get; set; } = "gpt-5-mini";
    /// <summary>The json_schema format name the client asks for, or null for prose.</summary>
    public string? Format { get; set; }
    public List<string> Tools { get; set; } = new();
    /// <summary>Top-level keys of the JSON body the client sent (values are never stored).</summary>
    public List<string> BodyKeys { get; set; } = new();
}

public sealed class CassetteResponse
{
    public int Status { get; set; } = 200;
    /// <summary>Delay before the status line and headers arrive.</summary>
    public int HeadersDelayMs { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CassetteChunk> Chunks { get; set; } = new();
}

public sealed class CassetteChunk
{
    public int DelayMs { get; set; }
    /// <summary>Raw text of the chunk (use for broken or non-JSON bodies).</summary>
    public string? Text { get; set; }
    /// <summary>A JSON value written out compactly (use for readable well-formed bodies).</summary>
    public JsonNode? Json { get; set; }

    public byte[] Bytes() => Encoding.UTF8.GetBytes(Text ?? Json?.ToJsonString(Cassettes.Compact) ?? "");
}

public sealed class CassetteFault
{
    /// <summary>
    /// "hang" (no reply until the client gives up), "connect-refused" (no connection), "reset-before-headers" (the
    /// connection drops after the request is sent), "cut-mid-chunk" (the body stops part-way through chunk
    /// <see cref="AtChunk"/> and the connection drops).
    /// </summary>
    public string Kind { get; set; } = "";
    public int AtChunk { get; set; }
}

public sealed class CassetteExpect
{
    /// <summary>"reply", "provider-failure", "timeout" or "invalid".</summary>
    public string Outcome { get; set; } = "reply";
    public string? Text { get; set; }
    public string? PointerTarget { get; set; }
    public int? Sources { get; set; }
    public string? FailureKind { get; set; }
    public int? Status { get; set; }
    public bool? Retryable { get; set; }
    /// <summary>The exact user-visible message of the exception.</summary>
    public string? Message { get; set; }
    /// <summary>Client timeout for the replay (ms); used by the timeout and slow-body cassettes.</summary>
    public int? TimeoutMs { get; set; }
}

public static class Cassettes
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Reply headers a cassette may keep. Everything else (request ids, org/project ids, cookies) is dropped.</summary>
    public static readonly IReadOnlySet<string> HeaderAllowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "content-type", "retry-after"
    };

    public static Cassette Load(string path) =>
        JsonSerializer.Deserialize<Cassette>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"{Path.GetFileName(path)} is not a cassette.");

    public static string Serialize(Cassette cassette) => JsonSerializer.Serialize(cassette, Options) + "\n";

    // Ids that could tie a recording to the account or a single request. Response/item ids are replaced, never kept.
    private static readonly Regex AccountIds = new(
        @"\b(?:org-[A-Za-z0-9]{6,}|proj_[A-Za-z0-9]{6,}|req_[A-Za-z0-9]{6,}|user-[A-Za-z0-9]{6,})\b",
        RegexOptions.CultureInvariant);
    private static readonly Regex ItemIds = new(
        @"\b(resp|msg|rs|ws|fc|ci|caller|call)_[A-Za-z0-9]{8,}\b", RegexOptions.CultureInvariant);
    // OpenAI echoes a masked copy of a rejected key ("Incorrect API key provided: sk-ab*****wxyz"); even masked, the
    // visible ends are part of the key, so the whole token goes.
    private static readonly Regex MaskedKey = new(@"[^\s""'\\]*\*{4,}[^\s""'\\.,]*",RegexOptions.CultureInvariant);
    private static readonly string[] DroppedFields = { "user", "safety_identifier", "prompt_cache_key", "metadata", "organization", "project" };

    /// <summary>
    /// Makes recorded body text safe to commit: replaces response/item ids and account ids with fixed placeholders and
    /// blanks fields that can identify the caller. Throws if <paramref name="secret"/> (the key used) is still present.
    /// </summary>
    public static string RedactBody(string body, string? secret)
    {
        var text = body;
        try
        {
            if (JsonNode.Parse(text) is JsonObject root)
            {
                foreach (var field in DroppedFields)
                    if (root.ContainsKey(field))
                        root[field] = null;
                text = root.ToJsonString(Compact);
            }
        }
        catch (JsonException)
        {
            // Not JSON (an error page, a cut body): the regex pass below still applies.
        }

        text = MaskedKey.Replace(text, "[redacted-key]");
        text = AccountIds.Replace(text, "redacted");
        text = ItemIds.Replace(text, m => m.Groups[1].Value + "_redacted");
        RefuseSecrets(text, secret);
        return text;
    }

    public static Dictionary<string, string> RedactHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
    {
        var kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in headers)
            if (HeaderAllowlist.Contains(name))
                kept[name.ToLowerInvariant()] = string.Join(", ", values);
        return kept;
    }

    /// <summary>Throws when text holds the real key or anything key-shaped. Never prints the value.</summary>
    public static void RefuseSecrets(string text, string? secret)
    {
        if (!string.IsNullOrEmpty(secret) && text.Contains(secret, StringComparison.Ordinal))
            throw new InvalidOperationException("BOO-CASS-001: the key itself is in the recording; nothing was written.");
        if (KeyShape.IsMatch(text))
            throw new InvalidOperationException("BOO-CASS-001: key-shaped text is in the recording; nothing was written.");
    }

    /// <summary>The OpenAI key shape and a bearer header value (same idea as scripts/quality/key_scan.py).</summary>
    public static readonly Regex KeyShape = new(
        @"(?<![A-Za-z0-9_/-])sk-(?:(?:proj|svcacct|admin)-)?[A-Za-z0-9_-]{20,}|(?i:\bbearer\s+)[A-Za-z0-9._~+/-]{32,}",
        RegexOptions.CultureInvariant);
}
