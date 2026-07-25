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
}

public sealed record SearchQuery(IReadOnlyList<QueryTerm> Terms)
{
    public static readonly SearchQuery Empty = new([]);

    public bool IsEmpty => Terms.Count == 0;

    public IEnumerable<QueryTerm.TagMatch> TagTerms => Terms.OfType<QueryTerm.TagMatch>();

    /// <summary>
    /// Parses whitespace-separated terms. A leading <c>-</c> negates. Terms that normalize
    /// away to nothing are dropped, so stray punctuation cannot silently match everything.
    /// </summary>
    public static SearchQuery Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Empty;

        var terms = new List<QueryTerm>();
        var seen = new HashSet<(TagName Tag, bool Exclude)>();

        foreach (var token in raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var exclude = token[0] == '-';
            var body = exclude ? token[1..] : token;

            if (!TagName.TryParse(body, out var tag)) continue;
            if (!seen.Add((tag, exclude))) continue;

            terms.Add(new QueryTerm.TagMatch(tag, exclude));
        }

        return terms.Count == 0 ? Empty : new SearchQuery(terms);
    }
}
