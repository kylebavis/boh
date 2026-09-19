using System.Text.Json;
using Boh.Web.Services;

namespace Boh.Tests;

/// <summary>
/// Which sidecar field, if any, names the page a downloaded file came from. The fallback to
/// the typed-in gallery URL is the importer's job; this only decides whether there is
/// something better to use.
/// </summary>
public class GalleryDlSourceMapperTests
{
    private static string? PageUrl(string json)
    {
        using var document = JsonDocument.Parse(json);
        return GalleryDlSourceMapper.PageUrl(document.RootElement.Clone());
    }

    [Fact]
    public void post_url_names_the_page()
    {
        Assert.Equal(
            "https://www.instagram.com/p/abc123/",
            PageUrl("""{ "post_url": "https://www.instagram.com/p/abc123/", "num": 1 }"""));
    }

    [Fact]
    public void page_url_and_webpage_url_are_accepted_too()
    {
        Assert.Equal("https://example.invalid/gallery/attack/1", PageUrl("""{ "page_url": "https://example.invalid/gallery/attack/1" }"""));
        Assert.Equal("https://example.invalid/watch", PageUrl("""{ "webpage_url": "https://example.invalid/watch" }"""));
    }

    /// <summary>
    /// The field carrying the media file itself is present on nearly every extractor. Reading
    /// it would replace a durable page address with a CDN link that commonly expires.
    /// </summary>
    [Fact]
    public void The_media_url_is_not_mistaken_for_a_page()
    {
        Assert.Null(PageUrl("""{ "url": "https://cdn.example.invalid/9f8e7d.jpg" }"""));
        Assert.Null(PageUrl("""{ "file_url": "https://cdn.example.invalid/9f8e7d.jpg" }"""));
    }

    /// <summary>Booru extractors publish no page field, which is what the fallback exists for.</summary>
    [Fact]
    public void A_sidecar_with_no_page_field_yields_nothing()
    {
        Assert.Null(PageUrl("""{ "id": 42, "tag_string": "landscape", "file_url": "https://x.invalid/a.png" }"""));
        Assert.Null(PageUrl("{}"));
        Assert.Null(GalleryDlSourceMapper.PageUrl(null));
    }

    /// <summary>
    /// Reddit's permalink is a path. Guessing which host it belongs to is how a post ends up
    /// recording an address that goes nowhere.
    /// </summary>
    [Fact]
    public void A_relative_value_is_refused_rather_than_repaired()
    {
        Assert.Null(PageUrl("""{ "post_url": "/r/pics/comments/abc/title/" }"""));
    }

    [Fact]
    public void A_non_web_scheme_is_refused()
    {
        Assert.Null(PageUrl("""{ "post_url": "ftp://files.invalid/a.png" }"""));
        Assert.Null(PageUrl("""{ "post_url": "javascript:alert(1)" }"""));
    }

    [Fact]
    public void A_field_of_the_wrong_shape_falls_through_to_the_next()
    {
        Assert.Equal(
            "https://example.invalid/page",
            PageUrl("""{ "post_url": null, "page_url": "https://example.invalid/page" }"""));

        Assert.Equal(
            "https://example.invalid/page",
            PageUrl("""{ "post_url": ["https://wrong.invalid/"], "page_url": "https://example.invalid/page" }"""));
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed()
    {
        Assert.Equal("https://example.invalid/p/1", PageUrl("""{ "post_url": "  https://example.invalid/p/1  " }"""));
    }
}
