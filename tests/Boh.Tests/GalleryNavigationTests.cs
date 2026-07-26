using System.Net;

namespace Boh.Tests;

/// <summary>
/// Exercises the gallery through real HTTP requests. Both behaviours here failed in the
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

    [Fact]
    public async Task Next_advances_to_the_second_page()
    {
        using var app = new TestApp(pageSize: 2);
        var ids = await SeedAsync(app, 5);
        var client = app.CreateNonRedirectingClient();

        var first = await app.GetHtmlAsync(client, "/?page=1");
        var second = await app.GetHtmlAsync(client, "/?page=2");
        var third = await app.GetHtmlAsync(client, "/?page=3");

        Assert.Equal("Page 1 of 3", TestApp.PaginationLabel(first));
        Assert.Equal("Page 2 of 3", TestApp.PaginationLabel(second));
        Assert.Equal("Page 3 of 3", TestApp.PaginationLabel(third));

        // Newest first with a page size of 2: [5,4] [3,2] [1].
        Assert.Equal([ids[4], ids[3]], TestApp.PostIdsIn(first));
        Assert.Equal([ids[2], ids[1]], TestApp.PostIdsIn(second));
        Assert.Equal([ids[0]], TestApp.PostIdsIn(third));
    }

    [Fact]
    public async Task The_next_link_on_page_one_points_at_page_two()
    {
        using var app = new TestApp(pageSize: 2);
        await SeedAsync(app, 5);
        var client = app.CreateNonRedirectingClient();

        var html = await app.GetHtmlAsync(client, "/");

        Assert.Contains("href=\"/?page=2\"", html);
    }

    [Fact]
    public async Task Pagination_keeps_the_active_search()
    {
        using var app = new TestApp(pageSize: 2);
        var ids = await SeedAsync(app, 5);
        foreach (var id in ids.Take(3)) await app.TagAsync(id, "keeper");
        var client = app.CreateNonRedirectingClient();

        var second = await app.GetHtmlAsync(client, "/?page=2&q=keeper");

        // Three matches over two pages; the second holds the oldest one alone.
        Assert.Equal("Page 2 of 2", TestApp.PaginationLabel(second));
        Assert.Equal([ids[0]], TestApp.PostIdsIn(second));
        Assert.Contains("q=keeper", TestApp.FirstCardHref(second));
    }

    [Fact]
    public async Task A_page_past_the_end_clamps_to_the_last_page()
    {
        using var app = new TestApp(pageSize: 2);
        var ids = await SeedAsync(app, 5);
        var client = app.CreateNonRedirectingClient();

        var html = await app.GetHtmlAsync(client, "/?page=999");

        Assert.Equal("Page 3 of 3", TestApp.PaginationLabel(html));
        Assert.Equal([ids[0]], TestApp.PostIdsIn(html));
    }

    [Fact]
    public async Task Deleting_a_post_returns_to_the_search_it_was_opened_from()
    {
        using var app = new TestApp(pageSize: 40);
        var ids = await SeedAsync(app, 3);
        await app.TagAsync(ids[0], "keeper");
        await app.TagAsync(ids[1], "keeper");
        var client = app.CreateNonRedirectingClient();

        var detailUrl = TestApp.FirstCardHref(await app.GetHtmlAsync(client, "/?q=keeper"));
        Assert.Contains("q=keeper", detailUrl);

        var landing = await DeleteAsync(app, client, detailUrl);

        Assert.Equal(HttpStatusCode.Found, landing.StatusCode);
        Assert.Equal("/?q=keeper", landing.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Deleting_a_post_returns_to_the_page_it_was_opened_from()
    {
        using var app = new TestApp(pageSize: 2);
        await SeedAsync(app, 5);
        var client = app.CreateNonRedirectingClient();

        var detailUrl = TestApp.FirstCardHref(await app.GetHtmlAsync(client, "/?page=2"));
        var landing = await DeleteAsync(app, client, detailUrl);

        Assert.Equal("/?page=2", landing.Headers.Location?.OriginalString);
    }

    /// <summary>
    /// Deleting the only post on the last page overshoots the collection. The redirect still
    /// names that page; the gallery is what has to cope, by clamping rather than rendering an
    /// empty grid.
    /// </summary>
    [Fact]
    public async Task Deleting_the_last_post_on_the_final_page_does_not_strand_the_visitor()
    {
        using var app = new TestApp(pageSize: 2);
        await SeedAsync(app, 5);
        var client = app.CreateNonRedirectingClient();

        var detailUrl = TestApp.FirstCardHref(await app.GetHtmlAsync(client, "/?page=3"));
        var landing = await DeleteAsync(app, client, detailUrl);

        Assert.Equal("/?page=3", landing.Headers.Location?.OriginalString);

        var html = await app.GetHtmlAsync(client, landing.Headers.Location!.OriginalString);
        Assert.Equal("Page 2 of 2", TestApp.PaginationLabel(html));
        Assert.NotEmpty(TestApp.PostIdsIn(html));
    }

    [Fact]
    public async Task Deleting_a_post_reached_directly_returns_to_the_gallery()
    {
        using var app = new TestApp(pageSize: 40);
        var ids = await SeedAsync(app, 2);
        var client = app.CreateNonRedirectingClient();

        var landing = await DeleteAsync(app, client, $"/Posts/Detail/{ids[0]}");

        Assert.Equal("/", landing.Headers.Location?.OriginalString);
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
        Assert.DoesNotContain("q=", action);

        var fields = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = TestApp.FormValue(form, "__RequestVerificationToken"),
            ["q"] = TestApp.FormValue(form, "q"),
            ["fromPage"] = TestApp.FormValue(form, "fromPage"),
        };

        return await client.PostAsync(action, new FormUrlEncodedContent(fields));
    }
}
