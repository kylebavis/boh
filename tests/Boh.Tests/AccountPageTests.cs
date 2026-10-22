using System.Text.RegularExpressions;
using Boh.Web;

namespace Boh.Tests;

/// <summary>
/// Checks the markup contract the theme code depends on. Like the tag-admin page tests, none
/// of this has a server-side symptom when it breaks: a renamed id or a dropped attribute
/// leaves the page rendering perfectly well in the wrong colors.
/// </summary>
/// <remarks>
/// <see cref="TestApp"/> boots with <c>BOH_AUTH_MODE=none</c>, so this covers the variant
/// with no account to store against. The signed-in path is exercised through
/// <see cref="ThemeTests"/> and the service tests, where the ticket and the row are visible.
/// </remarks>
public class AccountPageTests
{
    private const string Url = "/Account";

    [Test]
    public async Task The_header_carries_the_theme_toggle()
    {
        using var app = new TestApp();
        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), "/");

        await Assert.That(html).Contains("id=\"theme-toggle\"");

        // The icon shown is picked by CSS off data-theme-choice, so all three have to be
        // in the document for the toggle to have anything to switch between.
        await Assert.That(html).Contains("icon-auto");
        await Assert.That(html).Contains("icon-light");
        await Assert.That(html).Contains("icon-dark");
    }

    [Test]
    public async Task The_account_page_offers_a_palette_for_each_mode()
    {
        using var app = new TestApp();
        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), Url);

        await Assert.That(html).Contains("id=\"theme-form\"");

        foreach (var mode in new[] { Themes.LightMode, Themes.DarkMode })
        {
            var select = Regex.Match(html, "<select[^>]*data-theme-mode=\"" + mode + "\"[^>]*>");
            await Assert.That(select.Success).IsTrue().Because($"no select for {mode}");
        }
    }

    /// <summary>
    /// Each list must offer only the schemes built for that background, plus the stock look.
    /// Offering a dark scheme on the light side would let someone pick dark-on-dark.
    /// </summary>
    [Test]
    [Arguments("light")]
    [Arguments("dark")]
    public async Task Each_list_offers_only_the_palettes_for_its_own_mode(string mode)
    {
        using var app = new TestApp();
        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), Url);

        var block = Regex.Match(
            html, "<select[^>]*data-theme-mode=\"" + mode + "\"[^>]*>(.*?)</select>", RegexOptions.Singleline);
        await Assert.That(block.Success).IsTrue().Because($"no select for {mode}");

        var offered = Regex.Matches(block.Groups[1].Value, "value=\"([^\"]*)\"")
            .Select(m => m.Groups[1].Value)
            .ToList();

        await Assert.That(offered).Contains(Themes.Stock);
        await Assert.That(offered.Where(v => v != Themes.Stock).Order()).IsEquivalentTo(Themes.For(mode).Select(t => t.Id).Order(), CollectionOrdering.Matching);
    }

    /// <summary>
    /// app.js keys entirely off this attribute to decide whether it owns persistence, and
    /// when it does it cancels the submit. Getting it wrong in the "account" direction is
    /// silent and total: the Save button stops reaching the server at all.
    /// </summary>
    [Test]
    public async Task Without_accounts_the_form_stores_the_choice_in_the_browser()
    {
        using var app = new TestApp();

        await Assert.That(ThemeForm(await app.GetHtmlAsync(app.CreateNonRedirectingClient(), Url))).Contains("data-theme-store=\"local\"");
    }

    [Test]
    public async Task Signed_in_the_form_stores_the_choice_on_the_account()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        await Assert.That(ThemeForm(await app.GetHtmlAsync(client, Url))).Contains("data-theme-store=\"account\"");
    }

    /// <summary>
    /// The whole round trip on an instance with accounts: pick, save, and see it come back —
    /// which also covers the palettes reaching the layout, since those travel on the auth
    /// ticket rather than being read from the row at render time.
    /// </summary>
    [Test]
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
            }), TestContext.Current!.Execution.CancellationToken);
        await Assert.That(response.StatusCode).IsEqualTo(System.Net.HttpStatusCode.OK);

        // The gallery, not the account page: the point is that the choice reaches the layout
        // everywhere, before first paint.
        var home = await app.GetHtmlAsync(client, "/");
        await Assert.That(home).Contains("\"light\":\"solarized-light\"");
        await Assert.That(home).Contains("\"dark\":\"nord\"");
    }

    private static async Task<string> ThemeForm(string html)
    {
        var form = Regex.Match(html, "<form[^>]*id=\"theme-form\"[^>]*>");
        await Assert.That(form.Success).IsTrue().Because("no theme form on the account page");
        return form.Value;
    }

    /// <summary>
    /// The password form is the one thing that genuinely needs an account; the theme section
    /// has to survive its absence.
    /// </summary>
    [Test]
    public async Task Without_accounts_there_is_no_password_form()
    {
        using var app = new TestApp();
        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), Url);

        await Assert.That(html).DoesNotContain("name=\"currentPassword\"");
        await Assert.That(html).Contains("id=\"theme-form\"");
    }
}
