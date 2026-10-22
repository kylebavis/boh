using System.Text.Json;
using Boh.Web.Services;

namespace Boh.Tests;

public class GalleryDlTagMapperTests
{
    private static string[] Map(string json)
    {
        using var document = JsonDocument.Parse(json);
        return GalleryDlTagMapper.Map(document.RootElement.Clone())
            .Select(t => t.Display)
            .ToArray();
    }

    /// <summary>
    /// Danbooru-style extractors repeat every character, artist and copyright inside the
    /// general tag list. Emitting both spellings gives the post a redundant bare duplicate
    /// of a tag it already carries under a namespace.
    /// </summary>
    [Test]
    public async Task A_name_carried_by_a_namespace_is_not_repeated_unnamespaced()
    {
        var tags = Map("""
        {
            "tag_string": "pondering_my_orb ancient_wisdom_collection orb_enjoyer long_hair",
            "tag_string_character": "pondering_my_orb",
            "tag_string_copyright": "ancient_wisdom_collection",
            "tag_string_artist": "orb_enjoyer"
        }
        """);

        await Assert.That(tags).Contains("character:pondering_my_orb");
        await Assert.That(tags).DoesNotContain("pondering_my_orb");

        await Assert.That(tags).Contains("copyright:ancient_wisdom_collection");
        await Assert.That(tags).DoesNotContain("ancient_wisdom_collection");

        await Assert.That(tags).Contains("artist:orb_enjoyer");
        await Assert.That(tags).DoesNotContain("orb_enjoyer");

        // Genuinely general tags still come through bare.
        await Assert.That(tags).Contains("long_hair");
    }

    [Test]
    public async Task The_same_deduplication_applies_to_array_valued_fields()
    {
        var tags = Map("""
        { "tags": ["pondering_my_orb", "landscape"], "character": ["pondering_my_orb"] }
        """);

        await Assert.That(tags).IsEquivalentTo(["character:pondering_my_orb", "landscape"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task General_tags_survive_when_nothing_claims_them()
    {
        var tags = Map("""{ "tags": ["landscape", "sunset"] }""");

        await Assert.That(tags).IsEquivalentTo(["landscape", "sunset"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Maps_each_recognized_category_to_its_namespace()
    {
        var tags = Map("""
        {
            "artist": "foo",
            "character": "bar",
            "copyright": "baz",
            "rating": "safe",
            "category": "danbooru"
        }
        """);

        await Assert.That(tags).Contains("artist:foo");
        await Assert.That(tags).Contains("character:bar");
        await Assert.That(tags).Contains("copyright:baz");
        await Assert.That(tags).Contains("rating:safe");
        await Assert.That(tags).Contains("source:danbooru");
    }

    [Test]
    public async Task Reads_the_artist_from_a_nested_user_object()
    {
        var tags = Map("""{ "user": { "name": "Some Artist" } }""");

        await Assert.That(tags).IsEquivalentTo(["artist:some_artist"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Values_are_normalized_the_same_way_as_typed_tags()
    {
        var tags = Map("""{ "character": "Pondering My Orb" }""");

        await Assert.That(tags).IsEquivalentTo(["character:pondering_my_orb"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_space_separated_string_becomes_several_tags()
    {
        var tags = Map("""{ "tags": "a b c" }""");

        await Assert.That(tags).IsEquivalentTo(["a", "b", "c"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Duplicate_spellings_across_fields_collapse()
    {
        // Both spellings of the artist field are recognized; the tag should appear once.
        var tags = Map("""{ "artist": "foo", "tag_string_artist": "foo", "creator": "foo" }""");

        await Assert.That(tags).IsEquivalentTo(["artist:foo"], CollectionOrdering.Matching);
    }

    /// <summary>
    /// A value containing a colon must not have a namespace inferred from it. Previously the
    /// mapper concatenated "ns:value" and relied on where the first colon landed, so a bare
    /// general tag like "nier:automata" produced the bogus namespace "nier".
    /// </summary>
    [Test]
    public async Task A_colon_inside_a_value_does_not_invent_a_namespace()
    {
        var namespaced = Map("""{ "copyright": "nier:automata" }""");
        await Assert.That(namespaced).IsEquivalentTo(["copyright:nier:automata"], CollectionOrdering.Matching);

        var general = Map("""{ "tags": ["nier:automata"] }""");
        await Assert.That(general).IsEquivalentTo(["nier:automata"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Emoticon_tags_from_an_importer_survive()
    {
        var tags = Map("""{ "tags": ":d ^_^ >_<" }""");

        await Assert.That(tags).IsEquivalentTo([":d", "^_^", ">_<"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Unrecognized_fields_are_ignored_rather_than_guessed_at()
    {
        var tags = Map("""{ "score": 42, "file_url": "https://example.com/x.jpg", "id": 7 }""");

        await Assert.That(tags).IsEmpty();
    }

    [Test]
    [Arguments("{}")]
    [Arguments("""{ "tags": [] }""")]
    [Arguments("""{ "tags": "" }""")]
    [Arguments("""{ "tags": null }""")]
    public async Task Degenerate_metadata_yields_no_tags(string json)
    {
        await Assert.That(Map(json)).IsEmpty();
    }

    [Test]
    public async Task Non_object_metadata_yields_no_tags()
    {
        await Assert.That(GalleryDlTagMapper.Map(null)).IsEmpty();

        using var document = JsonDocument.Parse("[1,2,3]");
        await Assert.That(GalleryDlTagMapper.Map(document.RootElement.Clone())).IsEmpty();
    }
}
