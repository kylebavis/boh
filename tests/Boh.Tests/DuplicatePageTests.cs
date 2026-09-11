using System.Net;
using System.Text.RegularExpressions;
using ImageMagick;

namespace Boh.Tests;

/// <summary>
/// The pages that surface near-duplicates, rendered by the real application. What a unit
/// test cannot see here is whether the markup and the handlers agree — a renamed handler or a
/// mistyped <c>asp-page-handler</c> leaves a button that quietly does nothing.
/// </summary>
public class DuplicatePageTests
{
    [Fact]
    public async Task A_post_detail_page_lists_what_looks_like_it()
    {
        using var app = new TestApp();
        var original = await app.CreatePatternPostAsync(400);
        var repost = await app.CreatePatternPostAsync(160, MagickFormat.Jpeg);

        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), $"/Posts/Detail/{original}");

        Assert.Contains("Possible duplicates", html);
        Assert.Contains($"/Posts/Detail/{repost}", html);

        // The section offers the search that lists them all, not just the few it shows.
        Assert.Contains($"similar%3A{original}", html);
    }

    [Fact]
    public async Task An_ordinary_post_says_nothing_about_duplicates()
    {
        using var app = new TestApp();
        var first = await app.CreatePatternPostAsync(300, seed: 1);
        await app.CreatePatternPostAsync(300, seed: 2);

        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), $"/Posts/Detail/{first}");

        Assert.DoesNotContain("Possible duplicates", html);
    }

    [Fact]
    public async Task The_gallery_answers_a_similar_search()
    {
        using var app = new TestApp(pageSize: 40);
        var original = await app.CreatePatternPostAsync(400);
        var repost = await app.CreatePatternPostAsync(160);
        await app.CreatePatternPostAsync(300, seed: 2);

        var html = await app.GetHtmlAsync(
            app.CreateNonRedirectingClient(), $"/?q=similar%3A{original}");

        Assert.Equal([repost, original], TestApp.PostIdsIn(html));
    }

    [Fact]
    public async Task Maintenance_offers_both_duplicate_actions()
    {
        using var app = new TestApp();
        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), "/Maintenance");

        Assert.Contains("Compute missing perceptual hashes", html);
        Assert.Contains("Scan for possible duplicates", html);
    }

    [Fact]
    public async Task Running_the_hashing_pass_reports_that_there_is_nothing_to_do()
    {
        using var app = new TestApp();
        await app.CreatePatternPostAsync(300);

        // Uploads are hashed as they arrive, so a fresh collection has no backlog.
        var html = await SubmitAsync(app, "ComputeHashes");

        Assert.Contains("Every post that can be hashed already is", html);
    }

    [Fact]
    public async Task The_scan_reports_a_duplicate_it_found()
    {
        using var app = new TestApp();
        var original = await app.CreatePatternPostAsync(400);
        var repost = await app.CreatePatternPostAsync(160, MagickFormat.Jpeg);

        var html = await SubmitAsync(app, "ScanDuplicates");

        Assert.Contains("1 group(s) of look-alikes", html);
        Assert.Contains($"/Posts/Detail/{original}", html);
        Assert.Contains($"/Posts/Detail/{repost}", html);
    }

    [Fact]
    public async Task The_scan_says_so_when_nothing_looks_alike()
    {
        using var app = new TestApp();
        await app.CreatePatternPostAsync(300, seed: 1);
        await app.CreatePatternPostAsync(300, seed: 2);

        var html = await SubmitAsync(app, "ScanDuplicates");

        Assert.Contains("nothing looks like anything else", html);
    }

    /// <summary>
    /// Submits one of the maintenance forms the way the browser would, including the
    /// antiforgery token — which is what makes the handler name in the markup part of the test
    /// rather than something the test restates.
    /// </summary>
    private static async Task<string> SubmitAsync(TestApp app, string handler)
    {
        var client = app.CreateNonRedirectingClient();
        var page = await app.GetHtmlAsync(client, "/Maintenance");

        var form = Regex.Matches(page, "<form.*?</form>", RegexOptions.Singleline)
            .Select(m => m.Value)
            .Single(f => f.Contains($"handler={handler}", StringComparison.OrdinalIgnoreCase));

        var response = await client.PostAsync(
            TestApp.FormAction(form),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = TestApp.FormValue(form, "__RequestVerificationToken"),
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }
}
