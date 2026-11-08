using System.Text;
using System.Text.Json;

namespace Boo.Core;

public sealed record ResearchResult(string Text, IReadOnlyList<SourceLink> Sources, bool Sourced, string SourceStatus);

/// <summary>
/// Turns provider citations into a small, safe, de-duplicated list of
/// clickable HTTP(S) sources, and states plainly when an answer has none.
/// </summary>
public static class ResearchSources
{
    public const int MaximumSources = 8;
    public const int MaximumTitleLength = 120;
    public const string UnsourcedStatus = "No sources were returned. Treat this answer as unverified.";

    public static IReadOnlyList<SourceLink> Normalize(IEnumerable<SourceLink?> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var results = new List<SourceLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (results.Count == MaximumSources)
            {
                break;
            }

            if (candidate is null || !TryNormalizeUrl(candidate.Url, out var url, out var host))
            {
                continue;
            }

            if (!seen.Add(url))
            {
                continue;
            }

            results.Add(new SourceLink(NormalizeTitle(candidate.Title, host), url));
        }

        return results.AsReadOnly();
    }

    public static IReadOnlyList<SourceLink> Extract(JsonElement responseRoot)
    {
        if (responseRoot.ValueKind != JsonValueKind.Object ||
            !responseRoot.TryGetProperty("output", out var output) ||
            output.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SourceLink>();
        }

        var citations = new List<SourceLink?>();
        foreach (var item in output.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object ||
                    !part.TryGetProperty("annotations", out var annotations) ||
                    annotations.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var annotation in annotations.EnumerateArray())
                {
                    if (annotation.ValueKind != JsonValueKind.Object ||
                        GetString(annotation, "type") != "url_citation" ||
                        GetString(annotation, "url") is not { } url)
                    {
                        continue;
                    }

                    citations.Add(new SourceLink(GetString(annotation, "title") ?? url, url));
                }
            }
        }

        var primary = Normalize(citations);
        if (primary.Count > 0)
        {
            return primary;
        }

        var searched = new List<SourceLink?>();
        foreach (var item in output.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                GetString(item, "type") != "web_search_call" ||
                !item.TryGetProperty("action", out var action) ||
                action.ValueKind != JsonValueKind.Object ||
                !action.TryGetProperty("sources", out var sources) ||
                sources.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var source in sources.EnumerateArray())
            {
                if (source.ValueKind != JsonValueKind.Object || GetString(source, "url") is not { } url)
                {
                    continue;
                }

                searched.Add(new SourceLink(GetString(source, "title") ?? url, url));
            }
        }

        return Normalize(searched);
    }

    public static ResearchResult Finalize(string text, IEnumerable<SourceLink?> candidates)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sources = Normalize(candidates);
        var sourced = sources.Count > 0;
        var status = !sourced
            ? UnsourcedStatus
            : sources.Count == 1
                ? "1 source"
                : $"{sources.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} sources";
        return new ResearchResult(text, sources, sourced, status);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryNormalizeUrl(string? value, out string normalized, out string host)
    {
        normalized = string.Empty;
        host = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        host = uri.Host.ToLowerInvariant();
        var builder = new StringBuilder();
        builder.Append(scheme).Append("://").Append(host);
        if (!uri.IsDefaultPort)
        {
            builder.Append(':').Append(uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        builder.Append(uri.AbsolutePath);
        var query = uri.Query;
        if (query.Length > 1)
        {
            var kept = query[1..]
                .Split('&')
                .Where(parameter => parameter.Length > 0 &&
                    !parameter.Split('=', 2)[0].StartsWith("utm_", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (kept.Length > 0)
            {
                builder.Append('?').Append(string.Join('&', kept));
            }
        }

        normalized = builder.ToString();
        return true;
    }

    private static string NormalizeTitle(string? title, string host)
    {
        var cleaned = new string((title ?? string.Empty).Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            cleaned = host;
        }

        if (cleaned.Length > MaximumTitleLength)
        {
            cleaned = string.Concat(cleaned.AsSpan(0, MaximumTitleLength - 1), "…");
        }

        return cleaned;
    }
}
