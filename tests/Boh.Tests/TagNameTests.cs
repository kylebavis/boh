using Boh.Web.Tags;

namespace Boh.Tests;

public class TagNameTests
{
    [Test]
    [Arguments("landscape", "", "landscape")]
    [Arguments("  landscape  ", "", "landscape")]
    [Arguments("LandScape", "", "landscape")]
    [Arguments("artist:foo", "artist", "foo")]
    [Arguments("Artist:Foo", "artist", "foo")]
    [Arguments("meme:pondering_my_orb", "meme", "pondering_my_orb")]
    public async Task Parses_plain_and_namespaced_tags(string input, string ns, string name)
    {
        await Assert.That(TagName.TryParse(input, out var tag)).IsTrue();
        await Assert.That(tag.Namespace).IsEqualTo(ns);
        await Assert.That(tag.Name).IsEqualTo(name);
    }

    [Test]
    [Arguments("long hair", "long_hair")]
    [Arguments("long   hair", "long_hair")]
    [Arguments("a\tb", "a_b")]
    public async Task Collapses_internal_whitespace_to_underscore(string input, string expected)
    {
        await Assert.That(TagName.TryParse(input, out var tag)).IsTrue();
        await Assert.That(tag.Name).IsEqualTo(expected);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("&")]
    [Arguments(null)]
    public async Task Rejects_input_that_normalizes_to_nothing(string? input)
    {
        await Assert.That(TagName.TryParse(input, out _)).IsFalse();
    }

    [Test]
    public async Task Strips_disallowed_characters_without_leaving_doubled_underscores()
    {
        await Assert.That(TagName.TryParse("a & b", out var tag)).IsTrue();
        await Assert.That(tag.Name).IsEqualTo("a_b");
    }

    [Test]
    public async Task Keeps_characters_that_appear_in_real_booru_tags()
    {
        await Assert.That(TagName.TryParse("ranma_1/2_(series)", out var tag)).IsTrue();
        await Assert.That(tag.Name).IsEqualTo("ranma_1/2_(series)");

        await Assert.That(TagName.TryParse("d'artagnan", out var apostrophe)).IsTrue();
        await Assert.That(apostrophe.Name).IsEqualTo("d'artagnan");

        await Assert.That(TagName.TryParse("v.1.0", out var dotted)).IsTrue();
        await Assert.That(dotted.Name).IsEqualTo("v.1.0");
    }

    [Test]
    public async Task A_url_scheme_does_not_become_a_namespace()
    {
        await Assert.That(TagName.TryParse("https://example.com/x", out var tag)).IsTrue();
        await Assert.That(tag.Namespace).IsEqualTo("");
    }

    [Test]
    public async Task Only_the_first_colon_separates_the_namespace()
    {
        await Assert.That(TagName.TryParse("artist:foo:bar", out var tag)).IsTrue();
        await Assert.That(tag.Namespace).IsEqualTo("artist");
        await Assert.That(tag.Name).IsEqualTo("foo:bar");
    }

    [Test]
    [Arguments(":leading")]
    [Arguments("trailing:")]
    public async Task A_colon_at_an_edge_is_not_a_namespace_separator(string input)
    {
        await Assert.That(TagName.TryParse(input, out var tag)).IsTrue();
        await Assert.That(tag.Namespace).IsEqualTo("");
    }

    [Test]
    public async Task A_namespace_with_illegal_characters_is_treated_as_part_of_the_name()
    {
        await Assert.That(TagName.TryParse("we!rd:thing", out var tag)).IsTrue();
        await Assert.That(tag.Namespace).IsEqualTo("");
        // '!' disqualifies the prefix as a namespace; the punctuation itself is kept.
        await Assert.That(tag.Name).IsEqualTo("we!rd:thing");
    }

    [Test]
    public async Task Truncates_an_over_long_name()
    {
        var tag = (await Assert.That(TagName.TryParse(new string('a', 500), out var parsed) ? parsed : default).IsTypeOf<TagName>())!;

        await Assert.That(tag.Name.Length).IsEqualTo(TagName.MaxNameLength);
    }

    [Test]
    public async Task An_over_long_namespace_is_not_treated_as_a_namespace()
    {
        await Assert.That(TagName.TryParse(new string('n', 40) + ":x", out var tag)).IsTrue();
        await Assert.That(tag.Namespace).IsEqualTo("");
    }

    [Test]
    public async Task Display_round_trips_through_parsing()
    {
        await Assert.That(TagName.TryParse("meme:pondering_my_orb", out var tag)).IsTrue();
        await Assert.That(tag.Display).IsEqualTo("meme:pondering_my_orb");

        await Assert.That(TagName.TryParse(tag.Display, out var again)).IsTrue();
        await Assert.That(again).IsEqualTo(tag);
    }

    [Test]
    public async Task ParseMany_splits_on_whitespace_and_deduplicates_in_order()
    {
        var tags = TagName.ParseMany("  b  a:1   B  a:1 ");

        await Assert.That(tags.Count).IsEqualTo(2);
        await Assert.That(tags[0].Display).IsEqualTo("b");
        await Assert.That(tags[1].Display).IsEqualTo("a:1");
    }

    [Test]
    public async Task ParseMany_returns_empty_for_blank_input()
    {
        await Assert.That(TagName.ParseMany("   ")).IsEmpty();
        await Assert.That(TagName.ParseMany(null)).IsEmpty();
    }

    // ---- emoticon tags -------------------------------------------------

    /// <summary>
    /// Expression emoticons are ordinary booru vocabulary, not junk. The old ASCII-only charset
    /// erased them: ':d' was on 435 posts of a real collection and normalized to 'd'.
    /// </summary>
    [Test]
    [Arguments(":d")]
    [Arguments(";d")]
    [Arguments(":o")]
    [Arguments(":3")]
    [Arguments(":p")]
    [Arguments(":t")]
    [Arguments(":q")]
    [Arguments("^_^")]
    [Arguments(">_<")]
    [Arguments("=_=")]
    [Arguments(":<")]
    [Arguments(":>")]
    [Arguments(":|")]
    [Arguments(":/")]
    [Arguments("+_+")]
    [Arguments("@_@")]
    [Arguments("!")]
    [Arguments("?")]
    [Arguments("!?")]
    [Arguments("...")]
    [Arguments(">:(")]
    [Arguments(">:)")]
    [Arguments("^o^")]
    [Arguments(">o<")]
    [Arguments("\\m/")]
    [Arguments("\\||/")]
    public async Task Emoticon_tags_survive_verbatim(string input)
    {
        await Assert.That(TagName.TryParse(input, out var tag)).IsTrue();
        await Assert.That(tag.Namespace).IsEqualTo("");
        await Assert.That(tag.Name).IsEqualTo(input);
    }

    /// <summary>
    /// The damaging half of the old behavior was not dropping tags but merging distinct ones.
    /// These pairs are different expressions and must stay different tags.
    /// </summary>
    [Test]
    [Arguments(":d", ";d")]
    [Arguments(":o", ";o")]
    [Arguments(":o", "^o^")]
    [Arguments(":p", ";p")]
    [Arguments(":3", ";3")]
    [Arguments(">:(", ">:)")]
    [Arguments("...", "...?")]
    public async Task Distinct_emoticons_do_not_collapse_into_one_tag(string first, string second)
    {
        await Assert.That(TagName.TryParse(first, out var a)).IsTrue();
        await Assert.That(TagName.TryParse(second, out var b)).IsTrue();

        await Assert.That(b).IsNotEqualTo(a);
    }

    [Test]
    public async Task A_leading_colon_is_not_read_as_an_empty_namespace()
    {
        await Assert.That(TagName.TryParse(":d", out var tag)).IsTrue();
        await Assert.That(tag.Namespace).IsEqualTo("");
        await Assert.That(tag.Name).IsEqualTo(":d");
    }

    [Test]
    public async Task A_colon_after_non_namespace_characters_stays_in_the_name()
    {
        await Assert.That(TagName.TryParse(">:(", out var tag)).IsTrue();
        await Assert.That(tag.Namespace).IsEqualTo("");
        await Assert.That(tag.Name).IsEqualTo(">:(");
    }

    // ---- non-ASCII names -----------------------------------------------

    [Test]
    [Arguments("仁井学")]
    [Arguments("ボンボボン")]
    [Arguments("ドラクエ3")]
    public async Task Non_ascii_names_survive(string input)
    {
        await Assert.That(TagName.TryParse(input, out var tag)).IsTrue();
        await Assert.That(tag.Name).IsEqualTo(input);
    }

    /// <summary>
    /// Documents a known limitation rather than a desired behavior: the project builds with
    /// InvariantGlobalization, where Unicode normalization silently does nothing, so the two
    /// spellings of "café" remain distinct tags. Verified acceptable because the collection this
    /// was measured against contained no non-NFC names.
    /// </summary>
    [Test]
    public async Task Composed_and_decomposed_spellings_remain_distinct_without_normalization()
    {
        await Assert.That(TagName.TryParse("caf\u00e9", out var composed)).IsTrue();
        await Assert.That(TagName.TryParse("cafe\u0301", out var decomposed)).IsTrue();

        await Assert.That(composed.Name).IsEqualTo("caf\u00e9");
        // The combining mark is not a letter or digit, so it reduces to a separator and is
        // then trimmed from the end.
        await Assert.That(decomposed.Name).IsEqualTo("cafe");
        await Assert.That(decomposed).IsNotEqualTo(composed);
    }

    [Test]
    public async Task A_character_outside_the_bmp_is_judged_once_not_per_surrogate()
    {
        // U+20BB7 is a CJK ideograph in plane 2; iterating chars would reject each surrogate
        // half separately and yield two underscores instead of one character.
        await Assert.That(TagName.TryParse("a\U00020BB7b", out var tag)).IsTrue();
        await Assert.That(tag.Name).IsEqualTo("a\U00020BB7b");
    }

    [Test]
    public async Task An_emoji_is_not_a_letter_so_it_reduces_to_a_separator()
    {
        await Assert.That(TagName.TryParse("happy\U0001F600face", out var tag)).IsTrue();
        await Assert.That(tag.Name).IsEqualTo("happy_face");
    }

    // ---- explicit namespace --------------------------------------------

    /// <summary>
    /// A name containing a colon must not have its own namespace inferred when the caller
    /// already knows which namespace it belongs to.
    /// </summary>
    [Test]
    public async Task TryParseInNamespace_does_not_infer_a_namespace_from_the_name()
    {
        await Assert.That(TagName.TryParseInNamespace("series", "nier:automata", out var tag)).IsTrue();
        await Assert.That(tag.Namespace).IsEqualTo("series");
        await Assert.That(tag.Name).IsEqualTo("nier:automata");
    }

    [Test]
    public async Task TryParse_by_contrast_would_infer_one_from_a_bare_name()
    {
        // Demonstrates precisely why TryParseInNamespace exists.
        await Assert.That(TagName.TryParse("nier:automata", out var tag)).IsTrue();
        await Assert.That(tag.Namespace).IsEqualTo("nier");
    }

    [Test]
    public async Task TryParseInNamespace_normalizes_the_namespace()
    {
        await Assert.That(TagName.TryParseInNamespace("  Series  ", "foo", out var tag)).IsTrue();
        await Assert.That(tag.Namespace).IsEqualTo("series");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("has space")]
    [Arguments("has:colon")]
    public async Task An_unusable_namespace_degrades_to_an_unnamespaced_tag(string? ns)
    {
        await Assert.That(TagName.TryParseInNamespace(ns, "foo", out var tag)).IsTrue();
        await Assert.That(tag.Namespace).IsEqualTo("");
        await Assert.That(tag.Name).IsEqualTo("foo");
    }

    [Test]
    public async Task TryParseInNamespace_rejects_a_name_that_normalizes_to_nothing()
    {
        await Assert.That(TagName.TryParseInNamespace("series", "   ", out _)).IsFalse();
        await Assert.That(TagName.TryParseInNamespace("series", null, out _)).IsFalse();
    }

    [Test]
    public async Task TryParseInNamespace_still_collapses_whitespace()
    {
        await Assert.That(TagName.TryParseInNamespace("creator", "Some Artist", out var tag)).IsTrue();
        await Assert.That(tag.Namespace).IsEqualTo("creator");
        await Assert.That(tag.Name).IsEqualTo("some_artist");
    }

    [Test]
    public async Task Equality_is_by_value_so_tags_deduplicate()
    {
        TagName.TryParse("artist:foo", out var a);
        TagName.TryParse("Artist:FOO", out var b);
        await Assert.That(b).IsEqualTo(a);
    }
}
