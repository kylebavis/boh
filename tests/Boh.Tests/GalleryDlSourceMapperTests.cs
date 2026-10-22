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

    [Test]
    public async Task post_url_names_the_page()
    {
        await Assert.That(PageUrl("""{ "post_url": "https://www.instagram.com/p/abc123/", "num": 1 }""")).IsEqualTo("https://www.instagram.com/p/abc123/");
    }

    [Test]
    public async Task page_url_and_webpage_url_are_accepted_too()
    {
        await Assert.That(PageUrl("""{ "page_url": "https://example.invalid/gallery/attack/1" }""")).IsEqualTo("https://example.invalid/gallery/attack/1");
        await Assert.That(PageUrl("""{ "webpage_url": "https://example.invalid/watch" }""")).IsEqualTo("https://example.invalid/watch");
    }

    /// <summary>
    /// The field carrying the media file itself is present on nearly every extractor. Reading
    /// it would replace a durable page address with a CDN link that commonly expires.
    /// </summary>
    [Test]
    public async Task The_media_url_is_not_mistaken_for_a_page()
    {
        await Assert.That(PageUrl("""{ "url": "https://cdn.example.invalid/9f8e7d.jpg" }""")).IsNull();
        await Assert.That(PageUrl("""{ "file_url": "https://cdn.example.invalid/9f8e7d.jpg" }""")).IsNull();
    }

    /// <summary>Booru extractors publish no page field, which is what the fallback exists for.</summary>
    [Test]
    public async Task A_sidecar_with_no_page_field_yields_nothing()
    {
        await Assert.That(PageUrl("""{ "id": 42, "tag_string": "landscape", "file_url": "https://x.invalid/a.png" }""")).IsNull();
        await Assert.That(PageUrl("{}")).IsNull();
        await Assert.That(GalleryDlSourceMapper.PageUrl(null)).IsNull();
    }

    /// <summary>
    /// Reddit's permalink is a path. Guessing which host it belongs to is how a post ends up
    /// recording an address that goes nowhere.
    /// </summary>
    [Test]
    public async Task A_relative_value_is_refused_rather_than_repaired()
    {
        await Assert.That(PageUrl("""{ "post_url": "/r/pics/comments/abc/title/" }""")).IsNull();
    }

    [Test]
    public async Task A_non_web_scheme_is_refused()
    {
        await Assert.That(PageUrl("""{ "post_url": "ftp://files.invalid/a.png" }""")).IsNull();
        await Assert.That(PageUrl("""{ "post_url": "javascript:alert(1)" }""")).IsNull();
    }

    [Test]
    public async Task A_field_of_the_wrong_shape_falls_through_to_the_next()
    {
        await Assert.That(PageUrl("""{ "post_url": null, "page_url": "https://example.invalid/page" }""")).IsEqualTo("https://example.invalid/page");

        await Assert.That(PageUrl("""{ "post_url": ["https://wrong.invalid/"], "page_url": "https://example.invalid/page" }""")).IsEqualTo("https://example.invalid/page");
    }

    [Test]
    public async Task Surrounding_whitespace_is_trimmed()
    {
        await Assert.That(PageUrl("""{ "post_url": "  https://example.invalid/p/1  " }""")).IsEqualTo("https://example.invalid/p/1");
    }
}
