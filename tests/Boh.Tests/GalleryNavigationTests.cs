using System.Net;

namespace Boh.Tests;

/// <summary>
/// Exercises the gallery through real HTTP requests. Both behaviors here failed in the
/// running application while every unit test passed: the page number never bound, and
/// deleting a post threw away the search it had been found with.
/// </summary>
public class GalleryNavigationTests
{
    /// <summary>Distinct sizes so nothing deduplicates; ids ascend, and the gallery is newest first.</summary>
    private static async Task<int[]> SeedAsync(TestApp app, int count)
    {
        var ids = new List<int>();
        for (var i = 0; i < count; i++) ids.Add(await app.CreatePostAsync((uint)(16 + i)));
        return [.. ids];
    }

    [Test]
    public async Task Next_advances_to_the_second_page()
    {
        using var app = new TestApp(pageSize: 2);
        var ids = await SeedAsync(app, 5);
        var client = app.CreateNonRedirectingClient();

        var first = await app.GetHtmlAsync(client, "/?page=1");
        var second = await app.GetHtmlAsync(client, "/?page=2");
        var third = await app.GetHtmlAsync(client, "/?page=3");

        await Assert.That(TestApp.PaginationLabel(first)).IsEqualTo("Page 1 of 3");
        await Assert.That(TestApp.PaginationLabel(second)).IsEqualTo("Page 2 of 3");
        await Assert.That(TestApp.PaginationLabel(third)).IsEqualTo("Page 3 of 3");

        // Newest first with a page size of 2: [5,4] [3,2] [1].
        await Assert.That(TestApp.PostIdsIn(first)).IsEquivalentTo([ids[4], ids[3]], CollectionOrdering.Matching);
        await Assert.That(TestApp.PostIdsIn(second)).IsEquivalentTo([ids[2], ids[1]], CollectionOrdering.Matching);
        await Assert.That(TestApp.PostIdsIn(third)).IsEquivalentTo([ids[0]], CollectionOrdering.Matching);
    }

    [Test]
    public async Task The_next_link_on_page_one_points_at_page_two()
    {
        using var app = new TestApp(pageSize: 2);
        await SeedAsync(app, 5);
        var client = app.CreateNonRedirectingClient();

        var html = await app.GetHtmlAsync(client, "/");

        await Assert.That(html).Contains("href=\"/?page=2\"");
    }

    [Test]
    public async Task Pagination_keeps_the_active_search()
    {
        using var app = new TestApp(pageSize: 2);
        var ids = await SeedAsync(app, 5);
        foreach (var id in ids.Take(3)) await app.TagAsync(id, "keeper");
        var client = app.CreateNonRedirectingClient();

        var second = await app.GetHtmlAsync(client, "/?page=2&q=keeper");

        // Three matches over two pages; the second holds the oldest one alone.
        await Assert.That(TestApp.PaginationLabel(second)).IsEqualTo("Page 2 of 2");
        await Assert.That(TestApp.PostIdsIn(second)).IsEquivalentTo([ids[0]], CollectionOrdering.Matching);
        await Assert.That(TestApp.FirstCardHref(second)).Contains("q=keeper");
    }

    [Test]
    public async Task A_page_past_the_end_clamps_to_the_last_page()
    {
        using var app = new TestApp(pageSize: 2);
        var ids = await SeedAsync(app, 5);
        var client = app.CreateNonRedirectingClient();

        var html = await app.GetHtmlAsync(client, "/?page=999");

        await Assert.That(TestApp.PaginationLabel(html)).IsEqualTo("Page 3 of 3");
        await Assert.That(TestApp.PostIdsIn(html)).IsEquivalentTo([ids[0]], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Deleting_a_post_returns_to_the_search_it_was_opened_from()
    {
        using var app = new TestApp(pageSize: 40);
        var ids = await SeedAsync(app, 3);
        await app.TagAsync(ids[0], "keeper");
        await app.TagAsync(ids[1], "keeper");
        var client = app.CreateNonRedirectingClient();

        var detailUrl = TestApp.FirstCardHref(await app.GetHtmlAsync(client, "/?q=keeper"));
        await Assert.That(detailUrl).Contains("q=keeper");

        var landing = await DeleteAsync(app, client, detailUrl);

        await Assert.That(landing.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(landing.Headers.Location?.OriginalString).IsEqualTo("/?q=keeper");
    }

    [Test]
    public async Task Deleting_a_post_returns_to_the_page_it_was_opened_from()
    {
        using var app = new TestApp(pageSize: 2);
        await SeedAsync(app, 5);
        var client = app.CreateNonRedirectingClient();

        var detailUrl = TestApp.FirstCardHref(await app.GetHtmlAsync(client, "/?page=2"));
        var landing = await DeleteAsync(app, client, detailUrl);

        await Assert.That(landing.Headers.Location?.OriginalString).IsEqualTo("/?page=2");
    }

    /// <summary>
    /// Deleting the only post on the last page overshoots the collection. The redirect still
    /// names that page; the gallery is what has to cope, by clamping rather than rendering an
    /// empty grid.
    /// </summary>
    [Test]
    public async Task Deleting_the_last_post_on_the_final_page_does_not_strand_the_visitor()
    {
        using var app = new TestApp(pageSize: 2);
        await SeedAsync(app, 5);
        var client = app.CreateNonRedirectingClient();

        var detailUrl = TestApp.FirstCardHref(await app.GetHtmlAsync(client, "/?page=3"));
        var landing = await DeleteAsync(app, client, detailUrl);

        await Assert.That(landing.Headers.Location?.OriginalString).IsEqualTo("/?page=3");

        var html = await app.GetHtmlAsync(client, landing.Headers.Location!.OriginalString);
        await Assert.That(TestApp.PaginationLabel(html)).IsEqualTo("Page 2 of 2");
        await Assert.That(TestApp.PostIdsIn(html)).IsNotEmpty();
    }

    [Test]
    public async Task Deleting_a_post_reached_directly_returns_to_the_gallery()
    {
        using var app = new TestApp(pageSize: 40);
        var ids = await SeedAsync(app, 2);
        var client = app.CreateNonRedirectingClient();

        var landing = await DeleteAsync(app, client, $"/Posts/Detail/{ids[0]}");

        await Assert.That(landing.Headers.Location?.OriginalString).IsEqualTo("/");
    }

    /// <summary>
    /// Submits the detail page's own delete form — its declared action, its antiforgery token
    /// and its hidden fields — so this posts what a browser would rather than a hand-built
    /// request that might carry context the real form does not.
    /// </summary>
    private static async Task<HttpResponseMessage> DeleteAsync(TestApp app, HttpClient client, string detailUrl)
    {
        var form = TestApp.FormWithField(await app.GetHtmlAsync(client, detailUrl), "fromPage");
        var action = TestApp.FormAction(form);

        // asp-page-handler rebuilds the URL from route values and drops the query string, so
        // the action cannot carry the browsing context. That is exactly why the hidden fields
        // exist, and asserting it keeps this test from passing on a query string of its own.
        await Assert.That(action).DoesNotContain("q=");

        var fields = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = TestApp.FormValue(form, "__RequestVerificationToken"),
            ["q"] = TestApp.FormValue(form, "q"),
            ["fromPage"] = TestApp.FormValue(form, "fromPage"),
        };

        return await client.PostAsync(action, new FormUrlEncodedContent(fields));
    }
}
