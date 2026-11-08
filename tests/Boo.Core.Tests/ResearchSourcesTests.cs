using System.Linq;
using System.Text.Json;
using Boo.Core;

namespace Boo.Core.Tests;

public sealed class ResearchSourcesTests
{
    private const char Ellipsis = '\u2026';

    [Fact]
    public void Constants_match_the_contract()
    {
        Assert.Equal(8, ResearchSources.MaximumSources);
        Assert.Equal(120, ResearchSources.MaximumTitleLength);
        Assert.Equal("No sources were returned. Treat this answer as unverified.", ResearchSources.UnsourcedStatus);
    }

    // ---------- Normalize ----------

    [Fact]
    public void Normalize_rejects_a_null_sequence()
    {
        Assert.Throws<ArgumentNullException>(() => ResearchSources.Normalize(null!));
    }

    [Fact]
    public void Normalize_skips_null_entries()
    {
        var result = ResearchSources.Normalize(new SourceLink?[]
        {
            null,
            new("Docs", "https://example.com/docs"),
            null
        });

        Assert.Equal(new[] { new SourceLink("Docs", "https://example.com/docs") }, result);
    }

    [Fact]
    public void Normalize_of_an_empty_sequence_is_empty()
    {
        Assert.Empty(ResearchSources.Normalize(Array.Empty<SourceLink?>()));
    }

    [Theory]
    [InlineData("https://example.com/a/b")]
    [InlineData("http://example.org/page")]
    [InlineData("https://example.com/search?q=boo&page=2")]
    public void Normalize_keeps_clean_http_and_https_urls_unchanged(string url)
    {
        var link = Assert.Single(ResearchSources.Normalize(new SourceLink?[] { new("Title", url) }));
        Assert.Equal(url, link.Url);
        Assert.Equal("Title", link.Title);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://example.com/file.txt")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("data:text/html,hello")]
    [InlineData("/relative/path")]
    [InlineData("relative/path")]
    [InlineData("example.com/no-scheme")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://user:pass@example.com/secret")]
    [InlineData("https://user@example.com/secret")]
    [InlineData("https://exa\u0001mple.com/x")]
    [InlineData("https://example.com/a\nb")]
    [InlineData("https://example.com/a\u0007b")]
    public void Normalize_drops_urls_that_are_not_safe_absolute_http_links(string url)
    {
        var result = ResearchSources.Normalize(new SourceLink?[]
        {
            new("Bad", url),
            new("Good", "https://example.com/good")
        });

        Assert.Equal(new[] { new SourceLink("Good", "https://example.com/good") }, result);
    }

    [Fact]
    public void Normalize_removes_the_fragment()
    {
        var link = Assert.Single(ResearchSources.Normalize(new SourceLink?[] { new("T", "https://example.com/page#section-2") }));
        Assert.Equal("https://example.com/page", link.Url);
    }

    [Fact]
    public void Normalize_strips_utm_parameters_case_insensitively_and_keeps_the_rest_in_order()
    {
        var link = Assert.Single(ResearchSources.Normalize(new SourceLink?[]
        {
            new("T", "https://example.com/p?utm_source=news&b=2&UTM_Medium=email&a=1&Utm_Campaign=x")
        }));

        Assert.Equal("https://example.com/p?b=2&a=1", link.Url);
    }

    [Fact]
    public void Normalize_drops_the_question_mark_when_only_utm_parameters_were_present()
    {
        var link = Assert.Single(ResearchSources.Normalize(new SourceLink?[]
        {
            new("T", "https://example.com/p?utm_source=a&utm_medium=b")
        }));

        Assert.Equal("https://example.com/p", link.Url);
    }

    [Fact]
    public void Normalize_only_strips_parameters_whose_name_starts_with_utm_underscore()
    {
        var link = Assert.Single(ResearchSources.Normalize(new SourceLink?[]
        {
            new("T", "https://example.com/p?utm=1&xutm_source=2&utm_term=3&id=4")
        }));

        Assert.Equal("https://example.com/p?utm=1&xutm_source=2&id=4", link.Url);
    }

    [Fact]
    public void Normalize_removes_fragment_and_utm_together()
    {
        var link = Assert.Single(ResearchSources.Normalize(new SourceLink?[]
        {
            new("T", "https://example.com/p?id=7&utm_source=x#top")
        }));

        Assert.Equal("https://example.com/p?id=7", link.Url);
    }

    [Fact]
    public void Normalize_lower_cases_scheme_and_host_but_keeps_path_case()
    {
        var link = Assert.Single(ResearchSources.Normalize(new SourceLink?[]
        {
            new("T", "HTTPS://WWW.Example.COM/Docs/ReadMe")
        }));

        Assert.Equal("https://www.example.com/Docs/ReadMe", link.Url);
    }

    [Fact]
    public void Normalize_deduplicates_by_normalised_url_keeping_the_first_occurrence_in_order()
    {
        var result = ResearchSources.Normalize(new SourceLink?[]
        {
            new("First", "https://example.com/a?utm_source=feed"),
            new("Other", "https://example.com/b"),
            new("Second", "https://EXAMPLE.com/a#top"),
            new("Third", "HTTPS://example.com/a"),
            new("Other again", "https://example.com/b")
        });

        Assert.Equal(new[]
        {
            new SourceLink("First", "https://example.com/a"),
            new SourceLink("Other", "https://example.com/b")
        }, result);
    }

    [Fact]
    public void Normalize_does_not_merge_urls_that_differ_only_by_path_case()
    {
        var result = ResearchSources.Normalize(new SourceLink?[]
        {
            new("Lower", "https://example.com/page"),
            new("Upper", "https://example.com/Page")
        });

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Normalize_removes_control_characters_from_titles_and_trims_them()
    {
        var link = Assert.Single(ResearchSources.Normalize(new SourceLink?[]
        {
            new("  Hello\u0007 World\r\n ", "https://example.com/x")
        }));

        Assert.Equal("Hello World", link.Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("\u0001\u0002\t\n")]
    public void Normalize_uses_the_host_when_the_title_is_empty(string title)
    {
        var link = Assert.Single(ResearchSources.Normalize(new SourceLink?[]
        {
            new(title, "https://docs.example.com/guide")
        }));

        Assert.Equal("docs.example.com", link.Title);
    }

    [Fact]
    public void Normalize_truncates_long_titles_to_exactly_the_maximum_length_ending_in_an_ellipsis()
    {
        var link = Assert.Single(ResearchSources.Normalize(new SourceLink?[]
        {
            new(new string('a', 200), "https://example.com/x")
        }));

        Assert.Equal(ResearchSources.MaximumTitleLength, link.Title.Length);
        Assert.Equal(new string('a', 119) + Ellipsis, link.Title);
    }

    [Fact]
    public void Normalize_truncates_a_title_one_character_over_the_limit()
    {
        var title = new string('b', 120) + "c";
        var link = Assert.Single(ResearchSources.Normalize(new SourceLink?[] { new(title, "https://example.com/x") }));

        Assert.Equal(new string('b', 119) + Ellipsis, link.Title);
        Assert.Equal(120, link.Title.Length);
    }

    [Fact]
    public void Normalize_keeps_a_title_of_exactly_the_maximum_length()
    {
        var title = new string('t', 120);
        var link = Assert.Single(ResearchSources.Normalize(new SourceLink?[] { new(title, "https://example.com/x") }));

        Assert.Equal(title, link.Title);
    }

    [Fact]
    public void Normalize_measures_title_length_after_trimming()
    {
        var title = new string('t', 120);
        var link = Assert.Single(ResearchSources.Normalize(new SourceLink?[] { new("   " + title + "  ", "https://example.com/x") }));

        Assert.Equal(title, link.Title);
    }

    [Fact]
    public void Normalize_returns_at_most_eight_sources_in_input_order()
    {
        var candidates = Enumerable.Range(1, 12)
            .Select(i => (SourceLink?)new SourceLink($"Source {i}", $"https://example.com/{i}"))
            .ToArray();

        var result = ResearchSources.Normalize(candidates);

        Assert.Equal(8, result.Count);
        Assert.Equal(Enumerable.Range(1, 8).Select(i => $"https://example.com/{i}"), result.Select(s => s.Url));
    }

    [Fact]
    public void Normalize_applies_the_cap_after_dropping_invalid_and_duplicate_entries()
    {
        var candidates = new List<SourceLink?>
        {
            new("Dup A", "https://example.com/1"),
            new("Dup B", "https://example.com/1#again"),
            new("Bad", "javascript:void(0)"),
            null
        };
        candidates.AddRange(Enumerable.Range(2, 10).Select(i => (SourceLink?)new SourceLink($"S{i}", $"https://example.com/{i}")));

        var result = ResearchSources.Normalize(candidates);

        Assert.Equal(8, result.Count);
        Assert.Equal(Enumerable.Range(1, 8).Select(i => $"https://example.com/{i}"), result.Select(s => s.Url));
        Assert.Equal("Dup A", result[0].Title);
    }

    // ---------- Extract ----------

    [Fact]
    public void Extract_reads_url_citations_from_every_output_content_part()
    {
        var result = Extract("""
            {
              "output": [
                { "type": "web_search_call", "action": { "sources": [ { "url": "https://fallback.example.com/x", "title": "Fallback" } ] } },
                {
                  "type": "message",
                  "content": [
                    {
                      "type": "output_text",
                      "text": "Answer",
                      "annotations": [
                        { "type": "url_citation", "url": "https://one.example.com/a", "title": "One" },
                        { "type": "file_citation", "file_id": "f1" },
                        { "type": "url_citation", "url": "https://two.example.com/b?utm_source=openai", "title": "Two" }
                      ]
                    },
                    {
                      "type": "output_text",
                      "text": "More",
                      "annotations": [
                        { "type": "url_citation", "url": "https://three.example.com/c", "title": "Three" }
                      ]
                    }
                  ]
                }
              ]
            }
            """);

        Assert.Equal(new[]
        {
            new SourceLink("One", "https://one.example.com/a"),
            new SourceLink("Two", "https://two.example.com/b"),
            new SourceLink("Three", "https://three.example.com/c")
        }, result);
    }

    [Fact]
    public void Extract_uses_the_url_as_title_when_a_citation_has_no_title()
    {
        var result = Extract("""
            {"output":[{"type":"message","content":[{"type":"output_text","annotations":[
              {"type":"url_citation","url":"https://example.com/untitled"}
            ]}]}]}
            """);

        Assert.Equal(new[] { new SourceLink("https://example.com/untitled", "https://example.com/untitled") }, result);
    }

    [Fact]
    public void Extract_falls_back_to_web_search_call_sources_when_there_are_no_citations()
    {
        var result = Extract("""
            {
              "output": [
                {
                  "type": "web_search_call",
                  "action": {
                    "type": "search",
                    "sources": [
                      { "type": "url", "url": "https://a.example.com/one", "title": "A" },
                      { "type": "url", "url": "https://b.example.com/two" },
                      { "type": "api", "name": "no url here" }
                    ]
                  }
                },
                { "type": "message", "content": [ { "type": "output_text", "text": "Plain answer", "annotations": [] } ] },
                {
                  "type": "web_search_call",
                  "action": { "sources": [ { "url": "https://c.example.com/three#frag", "title": "C" } ] }
                }
              ]
            }
            """);

        Assert.Equal(new[]
        {
            new SourceLink("A", "https://a.example.com/one"),
            new SourceLink("https://b.example.com/two", "https://b.example.com/two"),
            new SourceLink("C", "https://c.example.com/three")
        }, result);
    }

    [Fact]
    public void Extract_ignores_web_search_sources_when_citations_exist()
    {
        var result = Extract("""
            {
              "output": [
                { "type": "web_search_call", "action": { "sources": [ { "url": "https://fallback.example.com/x", "title": "Fallback" } ] } },
                { "type": "message", "content": [ { "type": "output_text", "annotations": [
                  { "type": "url_citation", "url": "https://cited.example.com/y", "title": "Cited" }
                ] } ] }
              ]
            }
            """);

        Assert.Equal(new[] { new SourceLink("Cited", "https://cited.example.com/y") }, result);
    }

    [Fact]
    public void Extract_ignores_sources_on_items_that_are_not_web_search_calls()
    {
        var result = Extract("""
            {"output":[{"type":"message","action":{"sources":[{"url":"https://example.com/x","title":"X"}]},"content":[]}]}
            """);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"output\":null}")]
    [InlineData("{\"output\":{}}")]
    [InlineData("{\"output\":\"nope\"}")]
    [InlineData("{\"output\":[1,\"two\",null,[]]}")]
    [InlineData("{\"output\":[{\"content\":\"not an array\"}]}")]
    [InlineData("{\"output\":[{\"content\":[{\"annotations\":{}}]}]}")]
    [InlineData("{\"output\":[{\"content\":[{\"annotations\":[1,null,\"x\",{\"type\":5,\"url\":\"https://example.com/a\"}]}]}]}")]
    [InlineData("{\"output\":[{\"content\":[{\"annotations\":[{\"type\":\"url_citation\",\"url\":42,\"title\":\"T\"}]}]}]}")]
    [InlineData("{\"output\":[{\"content\":[{\"annotations\":[{\"type\":\"url_citation\"}]}]}]}")]
    [InlineData("{\"output\":[{\"type\":\"web_search_call\"}]}")]
    [InlineData("{\"output\":[{\"type\":\"web_search_call\",\"action\":\"search\"}]}")]
    [InlineData("{\"output\":[{\"type\":\"web_search_call\",\"action\":{\"sources\":{}}}]}")]
    [InlineData("{\"output\":[{\"type\":\"web_search_call\",\"action\":{\"sources\":[1,null,{\"url\":false},{\"title\":\"no url\"}]}}]}")]
    public void Extract_never_throws_on_missing_or_wrong_typed_properties(string json)
    {
        Assert.Empty(Extract(json));
    }

    [Fact]
    public void Extract_skips_malformed_entries_but_keeps_valid_neighbours()
    {
        var result = Extract("""
            {"output":[
              "junk",
              {"content":"junk"},
              {"type":"message","content":[
                 {"annotations":"junk"},
                 {"annotations":[{"type":"url_citation","url":42},{"type":"url_citation","url":"https://ok.example.com/1","title":7},{"type":"url_citation","url":"javascript:alert(1)","title":"bad"}]}
              ]}
            ]}
            """);

        var link = Assert.Single(result);
        Assert.Equal("https://ok.example.com/1", link.Url);
        // A wrong-typed title is treated as missing, so the url becomes the title.
        Assert.Equal("https://ok.example.com/1", link.Title);
    }

    [Fact]
    public void Extract_normalises_dedupes_and_caps_its_result()
    {
        var annotations = string.Join(",", Enumerable.Range(1, 12)
            .Select(i => $$"""{"type":"url_citation","url":"https://example.com/{{i}}?utm_source=openai","title":"S{{i}}"}"""));
        var duplicate = """{"type":"url_citation","url":"https://EXAMPLE.com/1#dup","title":"Duplicate"}""";
        var result = Extract($$"""{"output":[{"type":"message","content":[{"type":"output_text","annotations":[{{duplicate}},{{annotations}}]}]}]}""");

        Assert.Equal(8, result.Count);
        Assert.Equal(new SourceLink("Duplicate", "https://example.com/1"), result[0]);
        Assert.Equal(Enumerable.Range(1, 8).Select(i => $"https://example.com/{i}"), result.Select(s => s.Url));
    }

    // ---------- Finalize ----------

    [Fact]
    public void Finalize_rejects_null_text()
    {
        Assert.Throws<ArgumentNullException>(() => ResearchSources.Finalize(null!, Array.Empty<SourceLink?>()));
    }

    [Fact]
    public void Finalize_rejects_null_candidates()
    {
        Assert.Throws<ArgumentNullException>(() => ResearchSources.Finalize("text", null!));
    }

    [Fact]
    public void Finalize_with_no_usable_sources_is_unsourced()
    {
        var result = ResearchSources.Finalize("  The answer.\n", new SourceLink?[] { null, new("Bad", "javascript:alert(1)") });

        Assert.Equal("  The answer.\n", result.Text);
        Assert.Empty(result.Sources);
        Assert.False(result.Sourced);
        Assert.Equal(ResearchSources.UnsourcedStatus, result.SourceStatus);
    }

    [Fact]
    public void Finalize_with_one_source_says_one_source()
    {
        var result = ResearchSources.Finalize("Answer", new SourceLink?[] { new("Docs", "https://example.com/docs?utm_source=x") });

        Assert.Equal("Answer", result.Text);
        Assert.True(result.Sourced);
        Assert.Equal("1 source", result.SourceStatus);
        Assert.Equal(new[] { new SourceLink("Docs", "https://example.com/docs") }, result.Sources);
    }

    [Fact]
    public void Finalize_counts_normalised_sources()
    {
        var result = ResearchSources.Finalize("Answer", new SourceLink?[]
        {
            new("A", "https://a.example.com/x"),
            new("A again", "https://a.example.com/x#frag"),
            new("B", "https://b.example.com/y"),
            new("C", "https://c.example.com/z")
        });

        Assert.True(result.Sourced);
        Assert.Equal(3, result.Sources.Count);
        Assert.Equal("3 sources", result.SourceStatus);
    }

    [Fact]
    public void Finalize_reports_the_capped_count()
    {
        var candidates = Enumerable.Range(1, 11).Select(i => (SourceLink?)new SourceLink($"S{i}", $"https://example.com/{i}"));

        var result = ResearchSources.Finalize("Answer", candidates);

        Assert.Equal(8, result.Sources.Count);
        Assert.Equal("8 sources", result.SourceStatus);
    }

    private static IReadOnlyList<SourceLink> Extract(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ResearchSources.Extract(document.RootElement);
    }
}
