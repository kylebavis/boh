using Boh.Web.Tags;

namespace Boh.Tests;

public class SearchQueryTests
{
    private static QueryTerm.TagMatch[] TermsOf(string? raw) =>
        SearchQuery.Parse(raw).TagTerms.ToArray();

    [Test]
    public async Task Parses_positive_terms()
    {
        var terms = TermsOf("landscape meme:pondering_my_orb");

        await Assert.That(terms.Length).IsEqualTo(2);
        await Assert.That(terms).All(t => !(t.Exclude));
        await Assert.That(terms[0].Tag.Display).IsEqualTo("landscape");
        await Assert.That(terms[1].Tag.Display).IsEqualTo("meme:pondering_my_orb");
    }

    [Test]
    public async Task A_leading_dash_negates_a_term()
    {
        var terms = TermsOf("meme:pondering_my_orb -rating:explicit");

        await Assert.That(terms[0].Exclude).IsFalse();
        await Assert.That(terms[1].Exclude).IsTrue();
        await Assert.That(terms[1].Tag.Display).IsEqualTo("rating:explicit");
    }

    [Test]
    public async Task Search_terms_are_normalized_the_same_way_as_stored_tags()
    {
        var terms = TermsOf("Artist:Foo");

        await Assert.That(terms[0].Tag.Display).IsEqualTo("artist:foo");
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments(null)]
    public async Task Blank_input_is_an_empty_query(string? raw)
    {
        await Assert.That(SearchQuery.Parse(raw).IsEmpty).IsTrue();
    }

    [Test]
    public async Task Terms_that_normalize_to_nothing_are_dropped()
    {
        // A bare dash or stray punctuation must not turn into a term that matches everything.
        await Assert.That(SearchQuery.Parse("-").IsEmpty).IsTrue();
        // '&' and ',' are still outside the allowed charset; '!' is not, so it is a real tag now.
        await Assert.That(SearchQuery.Parse("& ,,,").IsEmpty).IsTrue();
    }

    [Test]
    public async Task Repeated_identical_terms_collapse()
    {
        await Assert.That(TermsOf("orb orb ORB")).HasSingleItem();
    }

    [Test]
    public async Task A_term_and_its_negation_are_kept_separately()
    {
        // Contradictory, but the caller decides what to do with it; the parser should not
        // silently discard one side.
        var terms = TermsOf("orb -orb");

        await Assert.That(terms.Length).IsEqualTo(2);
        await Assert.That(terms[0].Exclude).IsFalse();
        await Assert.That(terms[1].Exclude).IsTrue();
    }

    [Test]
    public async Task Extra_whitespace_between_terms_is_ignored()
    {
        await Assert.That(TermsOf("   a     b   ").Length).IsEqualTo(2);
    }

    // ---- source terms ----------------------------------------------------

    private static QueryTerm[] AllTermsOf(string? raw) => [.. SearchQuery.Parse(raw).Terms];

    [Test]
    public async Task A_url_prefix_searches_sources_rather_than_tags()
    {
        var term = (await Assert.That(AllTermsOf("url:twitter.com").Single()).IsTypeOf<QueryTerm.SourceMatch>())!;

        await Assert.That(term.Text).IsEqualTo("twitter.com");
        await Assert.That(term.Exclude).IsFalse();
    }

    [Test]
    public async Task A_url_term_can_be_negated()
    {
        var term = (await Assert.That(AllTermsOf("-url:twitter.com").Single()).IsTypeOf<QueryTerm.SourceMatch>())!;

        await Assert.That(term.Exclude).IsTrue();
    }

    /// <summary>
    /// The whole reason the prefix is <c>url:</c>: the importer stores gallery-dl's category
    /// in the <c>source</c> namespace, and those tags have to stay searchable.
    /// </summary>
    [Test]
    public async Task The_source_tag_namespace_is_untouched()
    {
        var term = (await Assert.That(AllTermsOf("source:twitter").Single()).IsTypeOf<QueryTerm.TagMatch>())!;

        await Assert.That(term.Tag.Display).IsEqualTo("source:twitter");
    }

    /// <summary>
    /// A URL is not a tag: routing it through tag normalization would replace the punctuation
    /// that makes it a URL with underscores, and it would then match nothing.
    /// </summary>
    [Test]
    public async Task A_url_keeps_the_punctuation_a_tag_would_lose()
    {
        var term = (await Assert.That(AllTermsOf("url:https://twitter.com/someone/status/123?x=1").Single()).IsTypeOf<QueryTerm.SourceMatch>())!;

        await Assert.That(term.Text).IsEqualTo("https://twitter.com/someone/status/123?x=1");
    }

    [Test]
    public async Task A_url_term_is_case_folded_like_everything_else()
    {
        var term = (await Assert.That(AllTermsOf("URL:Twitter.COM").Single()).IsTypeOf<QueryTerm.SourceMatch>())!;

        await Assert.That(term.Text).IsEqualTo("twitter.com");
    }

    [Test]
    public async Task url_none_is_a_keyword_rather_than_text_to_match()
    {
        var missing = (await Assert.That(AllTermsOf("url:none").Single()).IsTypeOf<QueryTerm.SourceMissing>())!;
        await Assert.That(missing.Exclude).IsFalse();

        var present = (await Assert.That(AllTermsOf("-url:none").Single()).IsTypeOf<QueryTerm.SourceMissing>())!;
        await Assert.That(present.Exclude).IsTrue();
    }

    /// <summary>A bare prefix states no condition, so it is dropped like an unparseable tag.</summary>
    [Test]
    public async Task A_url_prefix_with_nothing_after_it_is_dropped()
    {
        await Assert.That(SearchQuery.Parse("url:").IsEmpty).IsTrue();
        await Assert.That(SearchQuery.Parse("-url:").IsEmpty).IsTrue();
    }

    [Test]
    public async Task Repeated_identical_url_terms_collapse()
    {
        await Assert.That(AllTermsOf("url:twitter.com url:TWITTER.com")).HasSingleItem();
    }

    [Test]
    public async Task Tag_and_url_terms_mix_in_one_query()
    {
        var terms = AllTermsOf("landscape url:twitter.com -rating:explicit -url:none");

        await Assert.That(terms.Length).IsEqualTo(4);
        await Assert.That(terms[0]).IsTypeOf<QueryTerm.TagMatch>();
        await Assert.That(terms[1]).IsTypeOf<QueryTerm.SourceMatch>();
        await Assert.That(terms[2]).IsTypeOf<QueryTerm.TagMatch>();
        await Assert.That(terms[3]).IsTypeOf<QueryTerm.SourceMissing>();
    }

    // ---- similar terms ---------------------------------------------------

    [Test]
    public async Task A_similar_prefix_searches_by_appearance()
    {
        var term = (await Assert.That(AllTermsOf("similar:42").Single()).IsTypeOf<QueryTerm.SimilarTo>())!;

        await Assert.That(term.PostId).IsEqualTo(42);
        await Assert.That(term.Exclude).IsFalse();
    }

    [Test]
    public async Task A_similar_term_can_be_negated()
    {
        var term = (await Assert.That(AllTermsOf("-similar:42").Single()).IsTypeOf<QueryTerm.SimilarTo>())!;

        await Assert.That(term.Exclude).IsTrue();
        await Assert.That(term.PostId).IsEqualTo(42);
    }

    [Test]
    public async Task A_similar_term_is_case_folded_like_everything_else()
    {
        await Assert.That(AllTermsOf("SIMILAR:7").Single()).IsTypeOf<QueryTerm.SimilarTo>();
    }

    /// <summary>
    /// Anything that is not a post id states no condition. Dropped rather than read as a tag:
    /// <c>similar:cat</c> is a mistyped predicate, and matching the tag <c>similar:cat</c>
    /// instead would quietly answer a different question.
    /// </summary>
    [Test]
    [Arguments("similar:")]
    [Arguments("similar:cat")]
    [Arguments("similar:0")]
    [Arguments("similar:-3")]
    [Arguments("similar:12.5")]
    public async Task A_similar_term_without_a_post_id_is_dropped(string raw)
    {
        await Assert.That(SearchQuery.Parse(raw).IsEmpty).IsTrue();
    }

    [Test]
    public async Task Similar_mixes_with_tag_and_url_terms()
    {
        var terms = AllTermsOf("landscape similar:9 -url:none");

        await Assert.That(terms.Length).IsEqualTo(3);
        await Assert.That(terms[0]).IsTypeOf<QueryTerm.TagMatch>();
        await Assert.That(terms[1]).IsTypeOf<QueryTerm.SimilarTo>();
        await Assert.That(terms[2]).IsTypeOf<QueryTerm.SourceMissing>();
    }
}
