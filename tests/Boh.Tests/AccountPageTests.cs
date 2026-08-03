using System.Text.RegularExpressions;
using Boh.Web;

namespace Boh.Tests;

/// <summary>
/// Checks the markup contract the theme code depends on. Like the tag-admin page tests, none
/// of this has a server-side symptom when it breaks: a renamed id or a dropped attribute
/// leaves the page rendering perfectly well in the wrong colours.
/// </summary>
/// <remarks>
/// <see cref="TestApp"/> boots with <c>BOH_AUTH_MODE=none</c>, so this covers the variant
/// with no account to store against. The signed-in path is exercised through
/// <see cref="ThemeTests"/> and the service tests, where the ticket and the row are visible.
/// </remarks>
public class AccountPageTests
{
    private const string Url = "/Account";

    [Fact]
    public async Task The_header_carries_the_theme_toggle()
    {
        using var app = new TestApp();
        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), "/");

        Assert.Contains("id=\"theme-toggle\"", html);

        // The icon shown is picked by CSS off data-theme-choice, so all three have to be
        // in the document for the toggle to have anything to switch between.
        Assert.Contains("icon-auto", html);
        Assert.Contains("icon-light", html);
        Assert.Contains("icon-dark", html);
    }

    [Fact]
    public async Task The_account_page_offers_a_palette_for_each_mode()
    {
        using var app = new TestApp();
        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), Url);

        Assert.Contains("id=\"theme-form\"", html);

        foreach (var mode in new[] { Themes.LightMode, Themes.DarkMode })
        {
            var select = Regex.Match(html, "<select[^>]*data-theme-mode=\"" + mode + "\"[^>]*>");
            Assert.True(select.Success, $"no select for {mode}");
        }
    }

    /// <summary>
    /// Each list must offer only the schemes built for that background, plus the stock look.
    /// Offering a dark scheme on the light side would let someone pick dark-on-dark.
    /// </summary>
    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task Each_list_offers_only_the_palettes_for_its_own_mode(string mode)
    {
        using var app = new TestApp();
        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), Url);

        var block = Regex.Match(
            html, "<select[^>]*data-theme-mode=\"" + mode + "\"[^>]*>(.*?)</select>", RegexOptions.Singleline);
        Assert.True(block.Success, $"no select for {mode}");

        var offered = Regex.Matches(block.Groups[1].Value, "value=\"([^\"]*)\"")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.Contains(Themes.Stock, offered);
        Assert.Equal(Themes.For(mode).Select(t => t.Id).Order(), offered.Where(v => v != Themes.Stock).Order());
    }

    /// <summary>
    /// app.js keys entirely off this attribute to decide whether it owns persistence, and
    /// when it does it cancels the submit. Getting it wrong in the "account" direction is
    /// silent and total: the Save button stops reaching the server at all.
    /// </summary>
    [Fact]
    public async Task Without_accounts_the_form_stores_the_choice_in_the_browser()
    {
        using var app = new TestApp();

        Assert.Contains(
            "data-theme-store=\"local\"",
            ThemeForm(await app.GetHtmlAsync(app.CreateNonRedirectingClient(), Url)));
    }

    [Fact]
    public async Task Signed_in_the_form_stores_the_choice_on_the_account()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        Assert.Contains("data-theme-store=\"account\"", ThemeForm(await app.GetHtmlAsync(client, Url)));
    }

    /// <summary>
    /// The whole round trip on an instance with accounts: pick, save, and see it come back —
    /// which also covers the palettes reaching the layout, since those travel on the auth
    /// ticket rather than being read from the row at render time.
    /// </summary>
    [Fact]
    public async Task A_saved_palette_is_applied_on_the_next_page()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        var page = await app.GetHtmlAsync(client, Url);
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");

        var response = await client.PostAsync($"{Url}?handler=Theme", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["lightTheme"] = "solarized-light",
                ["darkTheme"] = "nord",
                ["__RequestVerificationToken"] = token.Groups[1].Value,
            }));
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        // The gallery, not the account page: the point is that the choice reaches the layout
        // everywhere, before first paint.
        var home = await app.GetHtmlAsync(client, "/");
        Assert.Contains("\"light\":\"solarized-light\"", home);
        Assert.Contains("\"dark\":\"nord\"", home);
    }

    private static string ThemeForm(string html)
    {
        var form = Regex.Match(html, "<form[^>]*id=\"theme-form\"[^>]*>");
        Assert.True(form.Success, "no theme form on the account page");
        return form.Value;
    }

    /// <summary>
    /// The password form is the one thing that genuinely needs an account; the theme section
    /// has to survive its absence.
    /// </summary>
    [Fact]
    public async Task Without_accounts_there_is_no_password_form()
    {
        using var app = new TestApp();
        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), Url);

        Assert.DoesNotContain("name=\"currentPassword\"", html);
        Assert.Contains("id=\"theme-form\"", html);
    }
}
