using System.Net;
using System.Text.RegularExpressions;
using Boh.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Boh.Tests;

/// <summary>
/// Curating a post's sources by hand, through the real page. An upload records nothing on
/// its own, so this is the only way one ever gets a source at all.
/// </summary>
public class PostSourceEditingTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private const string Origin = "https://example.invalid/posts/1";
    private const string Mirror = "https://elsewhere.invalid/art/42";

    /// <summary>The rendered source links, in the order the page lists them.</summary>
    private static string[] SourcesIn(string html)
    {
        var block = Regex.Match(html, "<ul class=\"source-list\">.*?</ul>", RegexOptions.Singleline);
        if (!block.Success) return [];

        return Regex.Matches(block.Value, "<a href=\"([^\"]+)\"")
            .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value))
            .ToArray();
    }

    /// <summary>
    /// Reads the URL off the button the page rendered rather than composing one, so the test
    /// exercises the same round trip the browser makes.
    /// </summary>
    private static string RemoveControlFor(string html, string url)
    {
        var label = WebUtility.HtmlEncode($"Remove source {url}");
        var button = Regex.Match(html, "<button[^>]* aria-label=\"" + Regex.Escape(label) + "\"[^>]*>");
        Assert.True(button.Success, $"no remove button for '{url}'");

        var hxPost = Regex.Match(button.Value, " hx-post=\"([^\"]+)\"");
        Assert.True(hxPost.Success, "the remove button posts nowhere");

        return WebUtility.HtmlDecode(hxPost.Groups[1].Value);
    }

    private static async Task<string> AddAsync(TestApp app, HttpClient client, int postId, string url)
    {
        var page = await app.GetHtmlAsync(client, $"/Posts/Detail/{postId}");

        var response = await TestApp.PostHxAsync(
            client, $"/Posts/Detail/{postId}?handler=AddSource", page,
            new Dictionary<string, string> { ["url"] = url });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task A_source_can_be_added_to_an_upload_that_had_none()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();
        var postId = await app.CreatePostAsync(41);

        Assert.Empty(SourcesIn(await app.GetHtmlAsync(client, $"/Posts/Detail/{postId}")));

        var fragment = await AddAsync(app, client, postId, Origin);

        // The fragment htmx swaps in is already the updated list.
        Assert.Equal([Origin], SourcesIn(fragment));
        Assert.Equal([Origin], SourcesIn(await app.GetHtmlAsync(client, $"/Posts/Detail/{postId}")));
    }

    [Fact]
    public async Task Adding_a_second_source_keeps_the_first_and_relabels_the_heading()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();
        var postId = await app.CreatePostAsync(42);

        var one = await AddAsync(app, client, postId, Origin);
        Assert.Contains("<h2>Source</h2>", one);

        var two = await AddAsync(app, client, postId, Mirror);

        Assert.Equal([Origin, Mirror], SourcesIn(two));

        // The heading is inside the swapped element precisely so this changes with it.
        Assert.Contains("<h2>Sources</h2>", two);
    }

    [Fact]
    public async Task A_source_can_be_removed_again()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();
        var postId = await app.CreatePostAsync(43);

        await AddAsync(app, client, postId, Origin);
        await AddAsync(app, client, postId, Mirror);

        var page = await app.GetHtmlAsync(client, $"/Posts/Detail/{postId}");
        var response = await TestApp.PostHxAsync(client, RemoveControlFor(page, Origin), page);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([Mirror], SourcesIn(await response.Content.ReadAsStringAsync()));
        Assert.Equal([Mirror], SourcesIn(await app.GetHtmlAsync(client, $"/Posts/Detail/{postId}")));
    }

    [Fact]
    public async Task Something_that_is_not_a_url_is_refused_with_a_reason()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();
        var postId = await app.CreatePostAsync(44);

        var fragment = await AddAsync(app, client, postId, "not a url");

        Assert.Contains("absolute http:// or https:// URL", fragment);
        Assert.Empty(SourcesIn(fragment));
    }

    /// <summary>An empty box is a slip, not a mistake worth an error message.</summary>
    [Fact]
    public async Task Submitting_an_empty_box_says_nothing_and_changes_nothing()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();
        var postId = await app.CreatePostAsync(45);

        var fragment = await AddAsync(app, client, postId, "   ");

        Assert.DoesNotContain("source-error", fragment);
        Assert.Empty(SourcesIn(fragment));
    }

    [Fact]
    public async Task Adding_a_url_the_post_already_has_leaves_one_entry()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();
        var postId = await app.CreatePostAsync(46);

        await AddAsync(app, client, postId, Origin);
        var again = await AddAsync(app, client, postId, Origin);

        Assert.Equal([Origin], SourcesIn(again));
    }

    /// <summary>
    /// Removal is keyed on the post as well as the row, so a source id belonging to another
    /// post cannot be deleted through this post's handler.
    /// </summary>
    [Fact]
    public async Task Removing_a_source_belonging_to_another_post_does_nothing()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();

        var mine = await app.CreatePostAsync(47);
        var theirs = await app.CreatePostAsync(48);

        await AddAsync(app, client, theirs, Mirror);

        int otherSourceId;
        using (var scope = app.Services.CreateScope())
        {
            var posts = scope.ServiceProvider.GetRequiredService<PostService>();
            otherSourceId = (await posts.GetAsync(theirs, Ct))!.Sources.Single().Id;

            Assert.False(await posts.RemoveSourceAsync(mine, otherSourceId, Ct));
        }

        var response = await TestApp.PostHxAsync(
            client,
            $"/Posts/Detail/{mine}?handler=RemoveSource&sourceId={otherSourceId}",
            await app.GetHtmlAsync(client, $"/Posts/Detail/{mine}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([Mirror], SourcesIn(await app.GetHtmlAsync(client, $"/Posts/Detail/{theirs}")));
    }

    /// <summary>
    /// The controls are hidden from a visitor who could not use them. The server enforces it
    /// too — that is <see cref="Boh.Web.Security.RequireAuthForWritesFilter"/>'s job — but a
    /// dead button on a public instance is its own bug.
    /// </summary>
    [Fact]
    public async Task A_signed_out_visitor_gets_no_editing_controls()
    {
        using var app = new TestApp(authMode: "password", publicRead: true);
        var client = app.CreateNonRedirectingClient();

        int postId;
        using (var scope = app.Services.CreateScope())
        {
            var posts = scope.ServiceProvider.GetRequiredService<PostService>();
            postId = Assert.IsType<PostCreateResult.Created>(await posts.CreateAsync(
                new MemoryStream(TestEnvironment.MakePng(49, 49)), null, Origin, Ct)).Post.Id;
        }

        var signedIn = await app.GetHtmlAsync(await app.SignInAsync(), $"/Posts/Detail/{postId}");
        Assert.Contains("handler=AddSource", signedIn);
        Assert.Contains("handler=RemoveSource", signedIn);

        var signedOut = await app.GetHtmlAsync(client, $"/Posts/Detail/{postId}");

        // The source itself is still shown; only the controls are gone.
        Assert.Equal([Origin], SourcesIn(signedOut));
        Assert.DoesNotContain("handler=AddSource", signedOut);
        Assert.DoesNotContain("handler=RemoveSource", signedOut);
    }
}
