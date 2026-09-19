namespace Boh.Web.Tags;

/// <summary>
/// One clause of a search. Modelled as a closed hierarchy rather than a bare tag list so
/// that metadata predicates (<c>width:&gt;1000</c>) and sorting (<c>order:score</c>) can be
/// added later without reshaping every caller.
/// </summary>
public abstract record QueryTerm
{
    private QueryTerm() { }

    /// <summary>Requires (or, when <paramref name="Exclude"/>, forbids) a tag on the post.</summary>
    public sealed record TagMatch(TagName Tag, bool Exclude) : QueryTerm;

    /// <summary>
    /// Requires (or forbids) a source URL containing <paramref name="Text"/>, which is already
    /// lowercased. Substring rather than exact: what people search for is a site — the host
    /// sits in the middle of the address, and nobody types a full URL into a search box.
    /// </summary>
    public sealed record SourceMatch(string Text, bool Exclude) : QueryTerm;

    /// <summary>
    /// Requires the post to have no source at all; <paramref name="Exclude"/> inverts it to
    /// "has at least one". This is the query that finds what still needs curating.
    /// </summary>
    public sealed record SourceMissing(bool Exclude) : QueryTerm;

    /// <summary>
    /// Requires (or forbids) the post to look like <paramref name="PostId"/>, matched on
    /// perceptual hash rather than on anything the post is tagged with. The reference post
    /// itself satisfies the term: putting it beside its look-alikes is the point of asking.
    /// </summary>
    public sealed record SimilarTo(int PostId, bool Exclude) : QueryTerm;
}

public sealed record SearchQuery(IReadOnlyList<QueryTerm> Terms)
{
    public static readonly SearchQuery Empty = new([]);

    public bool IsEmpty => Terms.Count == 0;

    public IEnumerable<QueryTerm.TagMatch> TagTerms => Terms.OfType<QueryTerm.TagMatch>();

    /// <summary>
    /// The prefix that searches source URLs. Deliberately not <c>source:</c>, which is
    /// already a tag namespace — the importer puts gallery-dl's category there, so posts
    /// carry tags like <c>source:twitter</c>. Taking that prefix for a URL predicate would
    /// leave those tags with no way to search for them.
    /// </summary>
    public const string SourcePrefix = "url:";

    /// <summary>
    /// The one <see cref="SourcePrefix"/> value that is a keyword rather than text to match.
    /// A source containing the literal word "none" is not reachable, which is the price of
    /// spelling this the way every booru does.
    /// </summary>
    public const string NoSourceKeyword = "none";

    /// <summary>
    /// The prefix that searches by appearance rather than by tag. Takes a post id, because a
    /// hash is not something anyone can type, and the only way to name a picture here is to
    /// point at a post that holds it.
    /// </summary>
    public const string SimilarPrefix = "similar:";

    /// <summary>
    /// Parses whitespace-separated terms. A leading <c>-</c> negates. Terms that normalize
    /// away to nothing are dropped, so stray punctuation cannot silently match everything.
    /// </summary>
    public static SearchQuery Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Empty;

        var terms = new List<QueryTerm>();

        // Structural equality across the whole hierarchy, so one set de-duplicates every kind
        // of term without each needing its own bookkeeping.
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
            // Not routed through TagName: a URL is not a tag and must keep its punctuation,
            // its case-folding is the only normalization it wants, and sanitizing it would
            // turn "//" and "?" into underscores that match nothing.
            var value = body[SourcePrefix.Length..].Trim().ToLowerInvariant();

            // A bare "url:" states no condition. Dropping it matches how an unparseable tag is
            // treated — better than matching every post or none of them.
            if (value.Length == 0) return null;

            return value == NoSourceKeyword
                ? new QueryTerm.SourceMissing(exclude)
                : new QueryTerm.SourceMatch(value, exclude);
        }

        if (body.StartsWith(SimilarPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // Anything that is not a post id states no condition. Dropped rather than treated
            // as a tag: "similar:cat" is a mistyped predicate, not a request for that tag.
            return int.TryParse(body[SimilarPrefix.Length..].Trim(), out var postId) && postId > 0
                ? new QueryTerm.SimilarTo(postId, exclude)
                : null;
        }

        return TagName.TryParse(body, out var tag) ? new QueryTerm.TagMatch(tag, exclude) : null;
    }
}
