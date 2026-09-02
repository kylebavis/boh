using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Boh.Web.Services;
using Boh.Web.Tags;

namespace Boh.Tests;

/// <summary>
/// Removing a tag has to act on the row that is actually there, not on a tag re-derived from
/// the label. Those two differ whenever a stored name contains a colon while its namespace is
/// empty, and a removal that resolves to a tag the post does not carry rewrites the post's
/// tags without dropping anything — so the page comes back unchanged and no error is raised.
/// </summary>
public class PostTagRemovalTests
{
    /// <summary>
    /// Some sites pack their own category into the tag text and hand the result over as one
    /// string, so an import from one of them consists entirely of <c>Category:Name</c> names
    /// arriving through the general tag list, where nothing supplies a namespace for them.
    /// </summary>
    private const string PackedCategorySidecar = """
    {
      "category": "example",
      "subcategory": "image",
      "id": 4365598,
      "tags": [
        "Artist:Orb Enjoyer",
        "Series:Ancient Wisdom Collection",
        "Character:Pondering My Orb",
        "Outfit:Pondering My Orb (Default Outfit)",
        "Theme:One Arm Up",
        "Source:Fan Art"
      ]
    }
    """;

    private static List<TagName> Imported()
    {
        using var document = JsonDocument.Parse(PackedCategorySidecar);
        return GalleryDlTagMapper.Map(document.RootElement.Clone());
    }

    /// <summary>
    /// Pins the storage shape the rest of this class is about: the site's category stays
    /// inside the name, because the general tag list carries no namespace for it.
    /// </summary>
    [Fact]
    public void A_site_category_packed_into_the_tag_text_is_stored_as_part_of_the_name()
    {
        Assert.Contains(new TagName("", "artist:orb_enjoyer"), Imported());
        Assert.Contains(new TagName("", "source:fan_art"), Imported());

        // The one name that does arrive with a namespace, as the control: it is addressable
        // either way round, so it kept working while the names above did not.
        Assert.Contains(new TagName("source", "example"), Imported());
    }

    [Fact]
    public async Task Every_tag_an_import_stored_can_be_removed_again()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();

        var postId = await app.CreatePostAsync(31);
        var imported = Imported();
        await app.TagAsync(postId, imported);

        Assert.Equal(imported.Count, (await app.ExplicitTagsAsync(postId)).Count);

        foreach (var tag in imported)
        {
            var html = await app.GetHtmlAsync(client, $"/Posts/Detail/{postId}");

            var response = await TestApp.PostHxAsync(client, RemoveControlFor(html, tag), html);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            Assert.DoesNotContain(tag, await app.ExplicitTagsAsync(postId));
        }

        Assert.Empty(await app.ExplicitTagsAsync(postId));
    }

    /// <summary>
    /// Reads the URL off the button the page renders rather than composing one. The failure
    /// this guards against lives in the gap between what the markup sends and what the
    /// handler can resolve, and a hand-built URL would step over that gap.
    /// </summary>
    private static string RemoveControlFor(string html, TagName tag)
    {
        var label = WebUtility.HtmlEncode($"Remove tag {tag.Display}");
        var button = Regex.Match(html, "<button[^>]* aria-label=\"" + Regex.Escape(label) + "\"[^>]*>");
        Assert.True(button.Success, $"no remove button for '{tag.Display}'");

        var url = Regex.Match(button.Value, " hx-post=\"([^\"]+)\"");
        Assert.True(url.Success, $"the remove button for '{tag.Display}' posts nowhere");

        return WebUtility.HtmlDecode(url.Groups[1].Value);
    }
}
