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
}
