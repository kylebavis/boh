namespace Boh.Web.Tags;

/// <summary>One clause of a search.</summary>
public abstract record QueryTerm
{
    private QueryTerm() { }

    /// <summary>Requires (or, when <paramref name="Exclude"/>, forbids) a tag on the post.</summary>
    public sealed record TagMatch(TagName Tag, bool Exclude) : QueryTerm;

    /// <summary>Source URL contains <paramref name="Text"/> (lowercased).</summary>
    public sealed record SourceMatch(string Text, bool Exclude) : QueryTerm;

    /// <summary>No source at all; <paramref name="Exclude"/> means at least one.</summary>
    public sealed record SourceMissing(bool Exclude) : QueryTerm;

    /// <summary>Looks like <paramref name="PostId"/> by perceptual hash; the post itself matches.</summary>
    public sealed record SimilarTo(int PostId, bool Exclude) : QueryTerm;
}

public sealed record SearchQuery(IReadOnlyList<QueryTerm> Terms)
{
    public static readonly SearchQuery Empty = new([]);

    public bool IsEmpty => Terms.Count == 0;

    public IEnumerable<QueryTerm.TagMatch> TagTerms => Terms.OfType<QueryTerm.TagMatch>();

    /// <summary>Not <c>source:</c>, which importers use as a tag namespace.</summary>
    public const string SourcePrefix = "url:";

    public const string NoSourceKeyword = "none";

    public const string SimilarPrefix = "similar:";

    /// <summary>Parses whitespace-separated terms; <c>-</c> negates, empty terms drop.</summary>
    public static SearchQuery Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Empty;

        var terms = new List<QueryTerm>();

        var seen = new HashSet<QueryTerm>();

        foreach (var token in raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var exclude = token[0] == '-';
            var body = exclude ? token[1..] : token;

            var term = ParseTerm(body, exclude);
            if (term is null || !seen.Add(term)) continue;

            terms.Add(term);
        }

        return terms.Count == 0 ? Empty : new SearchQuery(terms);
    }

    private static QueryTerm? ParseTerm(string body, bool exclude)
    {
        if (body.StartsWith(SourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            // Not a tag: keep punctuation, only lowercase.
            var value = body[SourcePrefix.Length..].Trim().ToLowerInvariant();

            // A bare "url:" states nothing; drop it.
            if (value.Length == 0) return null;

            return value == NoSourceKeyword
                ? new QueryTerm.SourceMissing(exclude)
                : new QueryTerm.SourceMatch(value, exclude);
        }

        if (body.StartsWith(SimilarPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // Not a post id: drop it rather than treat as a tag.
            return int.TryParse(body[SimilarPrefix.Length..].Trim(), out var postId) && postId > 0
                ? new QueryTerm.SimilarTo(postId, exclude)
                : null;
        }

        return TagName.TryParse(body, out var tag) ? new QueryTerm.TagMatch(tag, exclude) : null;
    }
}
