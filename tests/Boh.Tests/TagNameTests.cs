using Boh.Web.Tags;

namespace Boh.Tests;

public class TagNameTests
{
    [Theory]
    [InlineData("landscape", "", "landscape")]
    [InlineData("  landscape  ", "", "landscape")]
    [InlineData("LandScape", "", "landscape")]
    [InlineData("artist:foo", "artist", "foo")]
    [InlineData("Artist:Foo", "artist", "foo")]
    [InlineData("meme:pondering_my_orb", "meme", "pondering_my_orb")]
    public void Parses_plain_and_namespaced_tags(string input, string ns, string name)
    {
        Assert.True(TagName.TryParse(input, out var tag));
        Assert.Equal(ns, tag.Namespace);
        Assert.Equal(name, tag.Name);
    }

    [Theory]
    [InlineData("long hair", "long_hair")]
    [InlineData("long   hair", "long_hair")]
    [InlineData("a\tb", "a_b")]
    public void Collapses_internal_whitespace_to_underscore(string input, string expected)
    {
        Assert.True(TagName.TryParse(input, out var tag));
        Assert.Equal(expected, tag.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("&")]
    [InlineData(null)]
    public void Rejects_input_that_normalizes_to_nothing(string? input)
    {
        Assert.False(TagName.TryParse(input, out _));
    }

    [Fact]
    public void Strips_disallowed_characters_without_leaving_doubled_underscores()
    {
        Assert.True(TagName.TryParse("a & b", out var tag));
        Assert.Equal("a_b", tag.Name);
    }

    [Fact]
    public void Keeps_characters_that_appear_in_real_booru_tags()
    {
        Assert.True(TagName.TryParse("ranma_1/2_(series)", out var tag));
        Assert.Equal("ranma_1_2_(series)", tag.Name);

        Assert.True(TagName.TryParse("d'artagnan", out var apostrophe));
        Assert.Equal("d'artagnan", apostrophe.Name);

        Assert.True(TagName.TryParse("v.1.0", out var dotted));
        Assert.Equal("v.1.0", dotted.Name);
    }

    [Fact]
    public void A_url_scheme_does_not_become_a_namespace()
    {
        Assert.True(TagName.TryParse("https://example.com/x", out var tag));
        Assert.Equal("", tag.Namespace);
    }

    [Fact]
    public void Only_the_first_colon_separates_the_namespace()
    {
        Assert.True(TagName.TryParse("artist:foo:bar", out var tag));
        Assert.Equal("artist", tag.Namespace);
        Assert.Equal("foo_bar", tag.Name);
    }

    [Theory]
    [InlineData(":leading")]
    [InlineData("trailing:")]
    public void A_colon_at_an_edge_is_not_a_namespace_separator(string input)
    {
        Assert.True(TagName.TryParse(input, out var tag));
        Assert.Equal("", tag.Namespace);
    }

    [Fact]
    public void A_namespace_with_illegal_characters_is_treated_as_part_of_the_name()
    {
        Assert.True(TagName.TryParse("we!rd:thing", out var tag));
        Assert.Equal("", tag.Namespace);
        Assert.Equal("we_rd_thing", tag.Name);
    }

    [Fact]
    public void Truncates_an_over_long_name()
    {
        var tag = Assert.IsType<TagName>(
            TagName.TryParse(new string('a', 500), out var parsed) ? parsed : default);

        Assert.Equal(TagName.MaxNameLength, tag.Name.Length);
    }

    [Fact]
    public void An_over_long_namespace_is_not_treated_as_a_namespace()
    {
        Assert.True(TagName.TryParse(new string('n', 40) + ":x", out var tag));
        Assert.Equal("", tag.Namespace);
    }

    [Fact]
    public void Display_round_trips_through_parsing()
    {
        Assert.True(TagName.TryParse("meme:pondering_my_orb", out var tag));
        Assert.Equal("meme:pondering_my_orb", tag.Display);

        Assert.True(TagName.TryParse(tag.Display, out var again));
        Assert.Equal(tag, again);
    }

    [Fact]
    public void ParseMany_splits_on_whitespace_and_deduplicates_in_order()
    {
        var tags = TagName.ParseMany("  b  a:1   B  a:1 ");

        Assert.Equal(2, tags.Count);
        Assert.Equal("b", tags[0].Display);
        Assert.Equal("a:1", tags[1].Display);
    }

    [Fact]
    public void ParseMany_returns_empty_for_blank_input()
    {
        Assert.Empty(TagName.ParseMany("   "));
        Assert.Empty(TagName.ParseMany(null));
    }

    [Fact]
    public void Equality_is_by_value_so_tags_deduplicate()
    {
        TagName.TryParse("artist:foo", out var a);
        TagName.TryParse("Artist:FOO", out var b);
        Assert.Equal(a, b);
    }
}
