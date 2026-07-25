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
    [Fact]
    public void A_name_carried_by_a_namespace_is_not_repeated_unnamespaced()
    {
        var tags = Map("""
        {
            "tag_string": "pondering_my_orb ancient_wisdom_collection orb_enjoyer long_hair",
            "tag_string_character": "pondering_my_orb",
            "tag_string_copyright": "ancient_wisdom_collection",
            "tag_string_artist": "orb_enjoyer"
        }
        """);

        Assert.Contains("character:pondering_my_orb", tags);
        Assert.DoesNotContain("pondering_my_orb", tags);

        Assert.Contains("copyright:ancient_wisdom_collection", tags);
        Assert.DoesNotContain("ancient_wisdom_collection", tags);

        Assert.Contains("artist:orb_enjoyer", tags);
        Assert.DoesNotContain("orb_enjoyer", tags);

        // Genuinely general tags still come through bare.
        Assert.Contains("long_hair", tags);
    }

    [Fact]
    public void The_same_deduplication_applies_to_array_valued_fields()
    {
        var tags = Map("""
        { "tags": ["pondering_my_orb", "landscape"], "character": ["pondering_my_orb"] }
        """);

        Assert.Equal(["character:pondering_my_orb", "landscape"], tags);
    }

    [Fact]
    public void General_tags_survive_when_nothing_claims_them()
    {
        var tags = Map("""{ "tags": ["landscape", "sunset"] }""");

        Assert.Equal(["landscape", "sunset"], tags);
    }

    [Fact]
    public void Maps_each_recognized_category_to_its_namespace()
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

        Assert.Contains("artist:foo", tags);
        Assert.Contains("character:bar", tags);
        Assert.Contains("copyright:baz", tags);
        Assert.Contains("rating:safe", tags);
        Assert.Contains("source:danbooru", tags);
    }

    [Fact]
    public void Reads_the_artist_from_a_nested_user_object()
    {
        var tags = Map("""{ "user": { "name": "Some Artist" } }""");

        Assert.Equal(["artist:some_artist"], tags);
    }

    [Fact]
    public void Values_are_normalized_the_same_way_as_typed_tags()
    {
        var tags = Map("""{ "character": "Pondering My Orb" }""");

        Assert.Equal(["character:pondering_my_orb"], tags);
    }

    [Fact]
    public void A_space_separated_string_becomes_several_tags()
    {
        var tags = Map("""{ "tags": "a b c" }""");

        Assert.Equal(["a", "b", "c"], tags);
    }

    [Fact]
    public void Duplicate_spellings_across_fields_collapse()
    {
        // Both spellings of the artist field are recognized; the tag should appear once.
        var tags = Map("""{ "artist": "foo", "tag_string_artist": "foo", "creator": "foo" }""");

        Assert.Equal(["artist:foo"], tags);
    }

    [Fact]
    public void Unrecognized_fields_are_ignored_rather_than_guessed_at()
    {
        var tags = Map("""{ "score": 42, "file_url": "https://example.com/x.jpg", "id": 7 }""");

        Assert.Empty(tags);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "tags": [] }""")]
    [InlineData("""{ "tags": "" }""")]
    [InlineData("""{ "tags": null }""")]
    public void Degenerate_metadata_yields_no_tags(string json)
    {
        Assert.Empty(Map(json));
    }

    [Fact]
    public void Non_object_metadata_yields_no_tags()
    {
        Assert.Empty(GalleryDlTagMapper.Map(null));

        using var document = JsonDocument.Parse("[1,2,3]");
        Assert.Empty(GalleryDlTagMapper.Map(document.RootElement.Clone()));
    }
}
