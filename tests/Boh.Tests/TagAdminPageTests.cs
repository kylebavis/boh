using System.Text.RegularExpressions;

namespace Boh.Tests;

/// <summary>
/// Checks the contract the tag-admin page's client-side behaviour depends on: which fields
/// declare autocomplete, that each points at a panel that exists, and the markup the row
/// filter reads. The behaviour itself is browser-side and verified there; what these tests
/// prevent is the markup drifting out from under it — a renamed id or a dropped attribute
/// breaks the page silently, with no server-side symptom at all.
/// </summary>
public class TagAdminPageTests
{
    private const string Url = "/Tags/Admin";

    /// <summary>Every field on this page that names a tag, and the handler parameter it binds.</summary>
    public static TheoryData<string> TagFields => new() { "from", "to", "alias", "canonical", "child", "parent" };

    [Theory]
    [MemberData(nameof(TagFields))]
    public async Task Every_tag_field_completes_against_existing_tags(string name)
    {
        using var app = new TestApp();
        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), Url);

        var tag = Regex.Match(html, "<input[^>]*\\bname=\"" + name + "\"[^>]*>");
        Assert.True(tag.Success, $"no input named {name}");

        Assert.Contains("data-suggest-for=", tag.Value);
        Assert.Contains("hx-get=\"/Tags/Autocomplete\"", tag.Value);

        // These hold one tag, so a pick replaces the whole value rather than a token.
        Assert.Contains("data-suggest-single", tag.Value);
    }

    /// <summary>
    /// The panel id is the link between an input and its dropdown, in both directions: the
    /// click handler finds the input by querying for the panel's id. A typo would leave
    /// suggestions appearing but unclickable.
    /// </summary>
    [Theory]
    [MemberData(nameof(TagFields))]
    public async Task Each_field_points_at_a_panel_that_exists(string name)
    {
        using var app = new TestApp();
        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), Url);

        var tag = Regex.Match(html, "<input[^>]*\\bname=\"" + name + "\"[^>]*>").Value;
        var target = Regex.Match(tag, "data-suggest-for=\"#([^\"]+)\"").Groups[1].Value;

        Assert.NotEqual("", target);
        Assert.Contains($"id=\"{target}\" class=\"suggestions\"", html);

        // htmx must swap into the same panel the click handler reads back.
        Assert.Contains($"hx-target=\"#{target}\"", tag);
    }

    [Fact]
    public async Task The_autocomplete_endpoint_answers_the_parameter_the_fields_send()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();
        var id = await app.CreatePostAsync(24);
        await app.TagAsync(id, "meme:pondering_my_orb");

        var html = await app.GetHtmlAsync(client, "/Tags/Autocomplete?q=meme:pond");

        Assert.Contains("data-tag=\"meme:pondering_my_orb\"", html);
    }

    [Theory]
    [InlineData("#alias-table")]
    [InlineData("#implication-table")]
    public async Task Each_listing_has_a_filter_and_the_markup_it_relies_on(string table)
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();

        // The tables only render once they have rows, so seed one of each.
        await app.AddAliasAsync("orb", "meme:pondering_my_orb");
        await app.AddImplicationAsync("meme:pondering_my_orb", "meme");

        var html = await app.GetHtmlAsync(client, Url);

        var filter = Regex.Match(html, "<input[^>]*data-filter-table=\"" + Regex.Escape(table) + "\"[^>]*>");
        Assert.True(filter.Success, $"no filter input targeting {table}");

        // The filter reports its count into this element.
        var status = Regex.Match(filter.Value, "data-filter-status=\"#([^\"]+)\"").Groups[1].Value;
        Assert.Contains($"id=\"{status}\"", html);

        var markup = Regex.Match(html, Regex.Escape($"id=\"{table[1..]}\"") + ".*?</table>", RegexOptions.Singleline).Value;
        Assert.NotEqual("", markup);

        // Excluded from matching, so filtering for "remove" does not match every row.
        Assert.Contains("class=\"row-actions\"", markup);

        // Shown when a filter matches nothing, instead of a bare header.
        Assert.Contains("class=\"filter-empty\"", markup);
    }

    [Fact]
    public async Task The_capped_listings_still_scroll_horizontally_on_a_narrow_screen()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();
        await app.AddAliasAsync("orb", "meme:pondering_my_orb");

        var html = await app.GetHtmlAsync(client, Url);

        // Both axes: the cap is new, and dropping .scroll-x would reintroduce a page that
        // scrolls sideways on a phone.
        Assert.Contains("class=\"scroll-x scroll-y\"", html);
    }
}
