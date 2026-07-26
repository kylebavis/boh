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
        Assert.Equal("ranma_1/2_(series)", tag.Name);

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
        Assert.Equal("foo:bar", tag.Name);
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
        // '!' disqualifies the prefix as a namespace; the punctuation itself is kept.
        Assert.Equal("we!rd:thing", tag.Name);
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

    // ---- emoticon tags -------------------------------------------------

    /// <summary>
    /// Expression emoticons are ordinary booru vocabulary, not junk. The old ASCII-only charset
    /// erased them: ':d' was on 435 posts of a real collection and normalized to 'd'.
    /// </summary>
    [Theory]
    [InlineData(":d")]
    [InlineData(";d")]
    [InlineData(":o")]
    [InlineData(":3")]
    [InlineData(":p")]
    [InlineData(":t")]
    [InlineData(":q")]
    [InlineData("^_^")]
    [InlineData(">_<")]
    [InlineData("=_=")]
    [InlineData(":<")]
    [InlineData(":>")]
    [InlineData(":|")]
    [InlineData(":/")]
    [InlineData("+_+")]
    [InlineData("@_@")]
    [InlineData("!")]
    [InlineData("?")]
    [InlineData("!?")]
    [InlineData("...")]
    [InlineData(">:(")]
    [InlineData(">:)")]
    [InlineData("^o^")]
    [InlineData(">o<")]
    [InlineData("\\m/")]
    [InlineData("\\||/")]
    public void Emoticon_tags_survive_verbatim(string input)
    {
        Assert.True(TagName.TryParse(input, out var tag));
        Assert.Equal("", tag.Namespace);
        Assert.Equal(input, tag.Name);
    }

    /// <summary>
    /// The damaging half of the old behaviour was not dropping tags but merging distinct ones.
    /// These pairs are different expressions and must stay different tags.
    /// </summary>
    [Theory]
    [InlineData(":d", ";d")]
    [InlineData(":o", ";o")]
    [InlineData(":o", "^o^")]
    [InlineData(":p", ";p")]
    [InlineData(":3", ";3")]
    [InlineData(">:(", ">:)")]
    [InlineData("...", "...?")]
    public void Distinct_emoticons_do_not_collapse_into_one_tag(string first, string second)
    {
        Assert.True(TagName.TryParse(first, out var a));
        Assert.True(TagName.TryParse(second, out var b));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void A_leading_colon_is_not_read_as_an_empty_namespace()
    {
        Assert.True(TagName.TryParse(":d", out var tag));
        Assert.Equal("", tag.Namespace);
        Assert.Equal(":d", tag.Name);
    }

    [Fact]
    public void A_colon_after_non_namespace_characters_stays_in_the_name()
    {
        Assert.True(TagName.TryParse(">:(", out var tag));
        Assert.Equal("", tag.Namespace);
        Assert.Equal(">:(", tag.Name);
    }

    // ---- non-ASCII names -----------------------------------------------

    [Theory]
    [InlineData("仁井学")]
    [InlineData("ボンボボン")]
    [InlineData("ドラクエ3")]
    public void Non_ascii_names_survive(string input)
    {
        Assert.True(TagName.TryParse(input, out var tag));
        Assert.Equal(input, tag.Name);
    }

    /// <summary>
    /// Documents a known limitation rather than a desired behaviour: the project builds with
    /// InvariantGlobalization, where Unicode normalization silently does nothing, so the two
    /// spellings of "café" remain distinct tags. Verified acceptable because the collection this
    /// was measured against contained no non-NFC names.
    /// </summary>
    [Fact]
    public void Composed_and_decomposed_spellings_remain_distinct_without_normalization()
    {
        Assert.True(TagName.TryParse("caf\u00e9", out var composed));
        Assert.True(TagName.TryParse("cafe\u0301", out var decomposed));

        Assert.Equal("caf\u00e9", composed.Name);
        // The combining mark is not a letter or digit, so it reduces to a separator and is
        // then trimmed from the end.
        Assert.Equal("cafe", decomposed.Name);
        Assert.NotEqual(composed, decomposed);
    }

    [Fact]
    public void A_character_outside_the_bmp_is_judged_once_not_per_surrogate()
    {
        // U+20BB7 is a CJK ideograph in plane 2; iterating chars would reject each surrogate
        // half separately and yield two underscores instead of one character.
        Assert.True(TagName.TryParse("a\U00020BB7b", out var tag));
        Assert.Equal("a\U00020BB7b", tag.Name);
    }

    [Fact]
    public void An_emoji_is_not_a_letter_so_it_reduces_to_a_separator()
    {
        Assert.True(TagName.TryParse("happy\U0001F600face", out var tag));
        Assert.Equal("happy_face", tag.Name);
    }

    // ---- explicit namespace --------------------------------------------

    /// <summary>
    /// A name containing a colon must not have its own namespace inferred when the caller
    /// already knows which namespace it belongs to.
    /// </summary>
    [Fact]
    public void TryParseInNamespace_does_not_infer_a_namespace_from_the_name()
    {
        Assert.True(TagName.TryParseInNamespace("series", "nier:automata", out var tag));
        Assert.Equal("series", tag.Namespace);
        Assert.Equal("nier:automata", tag.Name);
    }

    [Fact]
    public void TryParse_by_contrast_would_infer_one_from_a_bare_name()
    {
        // Demonstrates precisely why TryParseInNamespace exists.
        Assert.True(TagName.TryParse("nier:automata", out var tag));
        Assert.Equal("nier", tag.Namespace);
    }

    [Fact]
    public void TryParseInNamespace_normalizes_the_namespace()
    {
        Assert.True(TagName.TryParseInNamespace("  Series  ", "foo", out var tag));
        Assert.Equal("series", tag.Namespace);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("has:colon")]
    public void An_unusable_namespace_degrades_to_an_unnamespaced_tag(string? ns)
    {
        Assert.True(TagName.TryParseInNamespace(ns, "foo", out var tag));
        Assert.Equal("", tag.Namespace);
        Assert.Equal("foo", tag.Name);
    }

    [Fact]
    public void TryParseInNamespace_rejects_a_name_that_normalizes_to_nothing()
    {
        Assert.False(TagName.TryParseInNamespace("series", "   ", out _));
        Assert.False(TagName.TryParseInNamespace("series", null, out _));
    }

    [Fact]
    public void TryParseInNamespace_still_collapses_whitespace()
    {
        Assert.True(TagName.TryParseInNamespace("creator", "Some Artist", out var tag));
        Assert.Equal("creator", tag.Namespace);
        Assert.Equal("some_artist", tag.Name);
    }

    [Fact]
    public void Equality_is_by_value_so_tags_deduplicate()
    {
        TagName.TryParse("artist:foo", out var a);
        TagName.TryParse("Artist:FOO", out var b);
        Assert.Equal(a, b);
    }
}
