using ImageMagick;

namespace Boh.Tests;

/// <summary>
/// The pages that surface near-duplicates, rendered by the real application. What a unit
/// test cannot see here is whether the markup and the handlers agree — a renamed handler or a
/// mistyped <c>asp-page-handler</c> leaves a button that quietly does nothing.
/// </summary>
public class DuplicatePageTests
{
    [Test]
    public async Task A_post_detail_page_lists_what_looks_like_it()
    {
        using var app = new TestApp();
        var original = await app.CreatePatternPostAsync(400);
        var repost = await app.CreatePatternPostAsync(160, MagickFormat.Jpeg);

        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), $"/Posts/Detail/{original}");

        await Assert.That(html).Contains("Possible duplicates");
        await Assert.That(html).Contains($"/Posts/Detail/{repost}");

        // The section offers the search that lists them all, not just the few it shows.
        await Assert.That(html).Contains($"similar%3A{original}");
    }

    [Test]
    public async Task An_ordinary_post_says_nothing_about_duplicates()
    {
        using var app = new TestApp();
        var first = await app.CreatePatternPostAsync(300, seed: 1);
        await app.CreatePatternPostAsync(300, seed: 2);

        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), $"/Posts/Detail/{first}");

        await Assert.That(html).DoesNotContain("Possible duplicates");
    }

    [Test]
    public async Task The_gallery_answers_a_similar_search()
    {
        using var app = new TestApp(pageSize: 40);
        var original = await app.CreatePatternPostAsync(400);
        var repost = await app.CreatePatternPostAsync(160);
        await app.CreatePatternPostAsync(300, seed: 2);

        var html = await app.GetHtmlAsync(
            app.CreateNonRedirectingClient(), $"/?q=similar%3A{original}");

        await Assert.That(TestApp.PostIdsIn(html)).IsEquivalentTo([repost, original], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Maintenance_offers_both_duplicate_actions()
    {
        using var app = new TestApp();
        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), "/Maintenance");

        await Assert.That(html).Contains("Compute missing perceptual hashes");
        await Assert.That(html).Contains("Scan for possible duplicates");
    }

    [Test]
    public async Task Running_the_hashing_pass_reports_that_there_is_nothing_to_do()
    {
        using var app = new TestApp();
        await app.CreatePatternPostAsync(300);

        // Uploads are hashed as they arrive, so a fresh collection has no backlog.
        var html = await SubmitAsync(app, "hashes");

        await Assert.That(html).Contains("Every post that can be hashed already is");
    }

    [Test]
    public async Task The_scan_reports_a_duplicate_it_found()
    {
        using var app = new TestApp();
        var original = await app.CreatePatternPostAsync(400);
        var repost = await app.CreatePatternPostAsync(160, MagickFormat.Jpeg);

        var html = await SubmitAsync(app, "duplicates");

        await Assert.That(html).Contains("1 group(s) of look-alikes");
        await Assert.That(html).Contains($"/Posts/Detail/{original}");
        await Assert.That(html).Contains($"/Posts/Detail/{repost}");
    }

    [Test]
    public async Task The_scan_says_so_when_nothing_looks_alike()
    {
        using var app = new TestApp();
        await app.CreatePatternPostAsync(300, seed: 1);
        await app.CreatePatternPostAsync(300, seed: 2);

        var html = await SubmitAsync(app, "duplicates");

        await Assert.That(html).Contains("nothing looks like anything else");
    }

    /// <summary>Starts a maintenance task from its button and returns the page once it has finished.</summary>
    private static Task<string> SubmitAsync(TestApp app, string task) =>
        app.SubmitAndWaitAsync("/Maintenance", $"task={task}");
}
