using System.Text.RegularExpressions;

namespace Boh.Tests;

/// <summary>
/// Checks the markup the autocomplete's keyboard handling stands on. Arrow-key selection is
/// browser-side, but it only works because each completing field declares itself a combobox
/// over a panel that exists, and because the rows the panel is filled with are options. Drop
/// one of those attributes and the list still renders and still takes a click — the failure
/// is silent, and only a keyboard user meets it.
/// </summary>
public class AutocompleteMarkupTests
{
    /// <summary>
    /// The completing fields outside the tag-admin page — <see cref="TagAdminPageTests"/>
    /// covers that page's six — as the field's name and a page that renders it.
    /// </summary>
    public static TheoryData<string, string> Fields => new()
    {
        // The header search, so every page carries it.
        { "q", "/" },
        { "tags", "detail" },
    };

    [Theory]
    [MemberData(nameof(Fields))]
    public async Task Every_completing_field_is_a_combobox_over_the_panel_it_fills(string name, string page)
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();

        // "detail" is a stand-in: the add-tags field only exists on a post that exists.
        var url = page == "detail" ? $"/Posts/Detail/{await app.CreatePostAsync(24)}" : page;
        var html = await app.GetHtmlAsync(client, url);

        var tag = Regex.Match(html, "<input[^>]*\\bname=\"" + name + "\"[^>]*>");
        Assert.True(tag.Success, $"no input named {name} on {url}");

        Assert.Contains("role=\"combobox\"", tag.Value);
        Assert.Contains("aria-autocomplete=\"list\"", tag.Value);

        // The dropdown's open state is announced by flipping this, so it has to start present.
        Assert.Contains("aria-expanded=\"false\"", tag.Value);

        var panel = Regex.Match(tag.Value, "data-suggest-for=\"#([^\"]+)\"").Groups[1].Value;
        Assert.NotEqual("", panel);
        Assert.Contains($"id=\"{panel}\" class=\"suggestions\"", html);

        // aria-controls has to name the same panel htmx swaps into, or a screen reader is
        // told about a list other than the one on screen.
        Assert.Contains($"aria-controls=\"{panel}\"", tag.Value);
        Assert.Contains($"hx-target=\"#{panel}\"", tag.Value);
    }

    /// <summary>
    /// The rows are what the arrow keys walk. They are marked as options so that highlighting
    /// one — which leaves focus in the input, and points aria-activedescendant at the row —
    /// reads as a selection rather than as nothing at all.
    /// </summary>
    [Fact]
    public async Task The_suggestion_rows_are_options_in_a_listbox()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();
        var id = await app.CreatePostAsync(24);
        await app.TagAsync(id, "meme:pondering_my_orb");

        var html = await app.GetHtmlAsync(client, "/Tags/Autocomplete?q=meme:pond");

        Assert.Contains("role=\"listbox\"", html);

        var row = Regex.Match(html, "<button[^>]*class=\"suggestion\"[^>]*>");
        Assert.True(row.Success, "no suggestion row rendered");
        Assert.Contains("role=\"option\"", row.Value);

        // Highlighting sets this to true on one row; without it there is nothing to flip.
        Assert.Contains("aria-selected=\"false\"", row.Value);
    }
}
