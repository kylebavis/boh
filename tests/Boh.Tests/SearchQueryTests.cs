using Boh.Web.Tags;

namespace Boh.Tests;

public class SearchQueryTests
{
    private static QueryTerm.TagMatch[] TermsOf(string? raw) =>
        SearchQuery.Parse(raw).TagTerms.ToArray();

    [Fact]
    public void Parses_positive_terms()
    {
        var terms = TermsOf("landscape meme:pondering_my_orb");

        Assert.Equal(2, terms.Length);
        Assert.All(terms, t => Assert.False(t.Exclude));
        Assert.Equal("landscape", terms[0].Tag.Display);
        Assert.Equal("meme:pondering_my_orb", terms[1].Tag.Display);
    }

    [Fact]
    public void A_leading_dash_negates_a_term()
    {
        var terms = TermsOf("meme:pondering_my_orb -rating:explicit");

        Assert.False(terms[0].Exclude);
        Assert.True(terms[1].Exclude);
        Assert.Equal("rating:explicit", terms[1].Tag.Display);
    }

    [Fact]
    public void Search_terms_are_normalized_the_same_way_as_stored_tags()
    {
        var terms = TermsOf("Artist:Foo");

        Assert.Equal("artist:foo", terms[0].Tag.Display);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Blank_input_is_an_empty_query(string? raw)
    {
        Assert.True(SearchQuery.Parse(raw).IsEmpty);
    }

    [Fact]
    public void Terms_that_normalize_to_nothing_are_dropped()
    {
        // A bare dash or stray punctuation must not turn into a term that matches everything.
        Assert.True(SearchQuery.Parse("-").IsEmpty);
        // '&' and ',' are still outside the allowed charset; '!' is not, so it is a real tag now.
        Assert.True(SearchQuery.Parse("& ,,,").IsEmpty);
    }

    [Fact]
    public void Repeated_identical_terms_collapse()
    {
        Assert.Single(TermsOf("orb orb ORB"));
    }

    [Fact]
    public void A_term_and_its_negation_are_kept_separately()
    {
        // Contradictory, but the caller decides what to do with it; the parser should not
        // silently discard one side.
        var terms = TermsOf("orb -orb");

        Assert.Equal(2, terms.Length);
        Assert.False(terms[0].Exclude);
        Assert.True(terms[1].Exclude);
    }

    [Fact]
    public void Extra_whitespace_between_terms_is_ignored()
    {
        Assert.Equal(2, TermsOf("   a     b   ").Length);
    }

    // ---- source terms ----------------------------------------------------

    private static QueryTerm[] AllTermsOf(string? raw) => [.. SearchQuery.Parse(raw).Terms];

    [Fact]
    public void A_url_prefix_searches_sources_rather_than_tags()
    {
        var term = Assert.IsType<QueryTerm.SourceMatch>(Assert.Single(AllTermsOf("url:twitter.com")));

        Assert.Equal("twitter.com", term.Text);
        Assert.False(term.Exclude);
    }

    [Fact]
    public void A_url_term_can_be_negated()
    {
        var term = Assert.IsType<QueryTerm.SourceMatch>(Assert.Single(AllTermsOf("-url:twitter.com")));

        Assert.True(term.Exclude);
    }

    /// <summary>
    /// The whole reason the prefix is <c>url:</c>: the importer stores gallery-dl's category
    /// in the <c>source</c> namespace, and those tags have to stay searchable.
    /// </summary>
    [Fact]
    public void The_source_tag_namespace_is_untouched()
    {
        var term = Assert.IsType<QueryTerm.TagMatch>(Assert.Single(AllTermsOf("source:twitter")));

        Assert.Equal("source:twitter", term.Tag.Display);
    }

    /// <summary>
    /// A URL is not a tag: routing it through tag normalization would replace the punctuation
    /// that makes it a URL with underscores, and it would then match nothing.
    /// </summary>
    [Fact]
    public void A_url_keeps_the_punctuation_a_tag_would_lose()
    {
        var term = Assert.IsType<QueryTerm.SourceMatch>(
            Assert.Single(AllTermsOf("url:https://twitter.com/someone/status/123?x=1")));

        Assert.Equal("https://twitter.com/someone/status/123?x=1", term.Text);
    }

    [Fact]
    public void A_url_term_is_case_folded_like_everything_else()
    {
        var term = Assert.IsType<QueryTerm.SourceMatch>(Assert.Single(AllTermsOf("URL:Twitter.COM")));

        Assert.Equal("twitter.com", term.Text);
    }

    [Fact]
    public void url_none_is_a_keyword_rather_than_text_to_match()
    {
        var missing = Assert.IsType<QueryTerm.SourceMissing>(Assert.Single(AllTermsOf("url:none")));
        Assert.False(missing.Exclude);

        var present = Assert.IsType<QueryTerm.SourceMissing>(Assert.Single(AllTermsOf("-url:none")));
        Assert.True(present.Exclude);
    }

    /// <summary>A bare prefix states no condition, so it is dropped like an unparseable tag.</summary>
    [Fact]
    public void A_url_prefix_with_nothing_after_it_is_dropped()
    {
        Assert.True(SearchQuery.Parse("url:").IsEmpty);
        Assert.True(SearchQuery.Parse("-url:").IsEmpty);
    }

    [Fact]
    public void Repeated_identical_url_terms_collapse()
    {
        Assert.Single(AllTermsOf("url:twitter.com url:TWITTER.com"));
    }

    [Fact]
    public void Tag_and_url_terms_mix_in_one_query()
    {
        var terms = AllTermsOf("landscape url:twitter.com -rating:explicit -url:none");

        Assert.Equal(4, terms.Length);
        Assert.IsType<QueryTerm.TagMatch>(terms[0]);
        Assert.IsType<QueryTerm.SourceMatch>(terms[1]);
        Assert.IsType<QueryTerm.TagMatch>(terms[2]);
        Assert.IsType<QueryTerm.SourceMissing>(terms[3]);
    }
}
