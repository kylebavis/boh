using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Boh.Web.Data;
using Boh.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Boh.Tests;

/// <summary>The JSON API under <c>/api/v1</c> and the bearer tokens that guard it.</summary>
public class ApiTests
{
    // ---- authentication ------------------------------------------------
    [Test]
    public async Task Without_a_token_a_private_instance_answers_401()
    {
        using var app = new TestApp(authMode: "password");

        var response = await app.CreateNonRedirectingClient().GetAsync("/api/v1/posts", TestContext.Current!.Execution.CancellationToken);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(response.Headers.WwwAuthenticate.Single().Scheme).IsEqualTo("Bearer");
    }

    /// <summary>The cookie is refused so a page elsewhere cannot drive the API with a visitor's session.</summary>
    [Test]
    public async Task A_signed_in_cookie_is_not_enough()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        var response = await client.GetAsync("/api/v1/posts", TestContext.Current!.Execution.CancellationToken);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task A_token_reads_and_writes()
    {
        using var app = new TestApp(authMode: "password");
        var client = await TokenClientAsync(app);
        var id = await app.CreatePostAsync(24);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/posts", cancellationToken: TestContext.Current!.Execution.CancellationToken);
        await Assert.That(list.GetProperty("posts")[0].GetProperty("id").GetInt32()).IsEqualTo(id);

        var tagged = await client.PostAsJsonAsync($"/api/v1/posts/{id}/tags", new { tags = new[] { "landscape" } }, cancellationToken: TestContext.Current!.Execution.CancellationToken);
        await Assert.That(tagged.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task An_unknown_token_is_refused()
    {
        using var app = new TestApp(authMode: "password");
        var client = app.CreateNonRedirectingClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "boh_not-a-real-token");

        await Assert.That((await client.GetAsync("/api/v1/posts", TestContext.Current!.Execution.CancellationToken)).StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task A_revoked_token_stops_working()
    {
        using var app = new TestApp(authMode: "password");
        var client = await TokenClientAsync(app);
        await Assert.That((await client.GetAsync("/api/v1/posts", TestContext.Current!.Execution.CancellationToken)).StatusCode).IsEqualTo(HttpStatusCode.OK);

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BohDbContext>();
            await db.ApiTokens.ExecuteDeleteAsync(cancellationToken: TestContext.Current!.Execution.CancellationToken);
        }

        await Assert.That((await client.GetAsync("/api/v1/posts", TestContext.Current!.Execution.CancellationToken)).StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Public_read_opens_reads_but_not_writes()
    {
        using var app = new TestApp(authMode: "password", publicRead: true);
        var client = app.CreateNonRedirectingClient();
        var id = await app.CreatePostAsync(24);

        await Assert.That((await client.GetAsync($"/api/v1/posts/{id}", TestContext.Current!.Execution.CancellationToken)).StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await client.DeleteAsync($"/api/v1/posts/{id}", TestContext.Current!.Execution.CancellationToken)).StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task With_auth_off_no_token_is_needed()
    {
        using var app = new TestApp();
        var id = await app.CreatePostAsync(24);

        var response = await app.CreateNonRedirectingClient().DeleteAsync($"/api/v1/posts/{id}", TestContext.Current!.Execution.CancellationToken);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    // ---- the account page ----------------------------------------------

    [Test]
    public async Task The_account_page_creates_a_token_that_works_and_revokes_it()
    {
        using var app = new TestApp(authMode: "password");
        var browser = await app.SignInAsync();

        var page = await app.GetHtmlAsync(browser, "/Account");
        var form = Regex.Matches(page, "<form.*?</form>", RegexOptions.Singleline)
            .Select(m => m.Value)
            .Single(f => f.Contains("handler=TokenCreate"));

        var created = await browser.PostAsync(TestApp.FormAction(form), new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["name"] = "shortcut",
                ["__RequestVerificationToken"] = TestApp.FormValue(form, "__RequestVerificationToken"),
            }), TestContext.Current!.Execution.CancellationToken);
        var html = await created.Content.ReadAsStringAsync(TestContext.Current!.Execution.CancellationToken);

        var secret = Regex.Match(html, "id=\"new-token\" value=\"(boh_[^\"]+)\"");
        await Assert.That(secret.Success).IsTrue().Because("the new token was not shown");

        var api = app.CreateNonRedirectingClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret.Groups[1].Value);
        await Assert.That((await api.GetAsync("/api/v1/posts", TestContext.Current!.Execution.CancellationToken)).StatusCode).IsEqualTo(HttpStatusCode.OK);

        var revoke = TestApp.FormWithField(await app.GetHtmlAsync(browser, "/Account"), "tokenId");
        var revoked = await browser.PostAsync(TestApp.FormAction(revoke), new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["tokenId"] = TestApp.FormValue(revoke, "tokenId"),
                ["__RequestVerificationToken"] = TestApp.FormValue(revoke, "__RequestVerificationToken"),
            }), TestContext.Current!.Execution.CancellationToken);
        await Assert.That(await revoked.Content.ReadAsStringAsync(TestContext.Current!.Execution.CancellationToken)).Contains("Token revoked.");
        await Assert.That((await api.GetAsync("/api/v1/posts", TestContext.Current!.Execution.CancellationToken)).StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    // ---- posts ---------------------------------------------------------

    [Test]
    public async Task Uploading_creates_a_tagged_post_with_its_source()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();

        var response = await client.PostAsync("/api/v1/posts",
            Upload(TestEnvironment.MakePng(24, 24), tags: "landscape artist:someone", source: "https://example.com/1"),
            TestContext.Current!.Execution.CancellationToken);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var post = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current!.Execution.CancellationToken);

        await Assert.That(post.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("display").GetString()).Order()).IsEquivalentTo<IEnumerable<string?>, string?>(["artist:someone", "landscape"], CollectionOrdering.Matching);
        await Assert.That(post.GetProperty("sources")[0].GetProperty("url").GetString()).IsEqualTo("https://example.com/1");
        await Assert.That(response.Headers.Location!.OriginalString).IsEqualTo($"/api/v1/posts/{post.GetProperty("id").GetInt32()}");
    }

    [Test]
    public async Task Uploading_a_stored_file_answers_409_naming_the_post()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();
        var file = TestEnvironment.MakePng(24, 24);

        var first = await (await client.PostAsync("/api/v1/posts", Upload(file), TestContext.Current!.Execution.CancellationToken)).Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current!.Execution.CancellationToken);
        var second = await client.PostAsync("/api/v1/posts", Upload(file), TestContext.Current!.Execution.CancellationToken);

        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current!.Execution.CancellationToken);
        await Assert.That(body.GetProperty("postId").GetInt32()).IsEqualTo(first.GetProperty("id").GetInt32());
    }

    [Test]
    public async Task Uploading_without_a_file_is_a_400()
    {
        using var app = new TestApp();

        var response = await app.CreateNonRedirectingClient().PostAsync("/api/v1/posts", new MultipartFormDataContent(), TestContext.Current!.Execution.CancellationToken);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Put_replaces_the_explicit_tags()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();
        var id = await app.CreatePostAsync(24);
        await app.TagAsync(id, "old", "keep");

        var response = await client.PutAsJsonAsync($"/api/v1/posts/{id}/tags", new { tags = new[] { "keep", "new" } }, cancellationToken: TestContext.Current!.Execution.CancellationToken);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await app.ExplicitTagsAsync(id)).Select(t => t.Display).Order()).IsEquivalentTo(["keep", "new"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Tagging_a_missing_post_is_a_404()
    {
        using var app = new TestApp();

        var response = await app.CreateNonRedirectingClient()
            .PostAsJsonAsync("/api/v1/posts/999/tags", new { tags = new[] { "x" } }, cancellationToken: TestContext.Current!.Execution.CancellationToken);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Search_and_random_follow_the_query()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();
        var tagged = await app.CreatePostAsync(24);
        await app.CreatePostAsync(32);
        await app.TagAsync(tagged, "landscape");

        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/posts?q=landscape", cancellationToken: TestContext.Current!.Execution.CancellationToken);
        await Assert.That(list.GetProperty("totalCount").GetInt32()).IsEqualTo(1);

        var random = await client.GetFromJsonAsync<JsonElement>("/api/v1/posts/random?q=landscape", cancellationToken: TestContext.Current!.Execution.CancellationToken);
        await Assert.That(random.GetProperty("id").GetInt32()).IsEqualTo(tagged);

        await Assert.That((await client.GetAsync("/api/v1/posts/random?q=nothing_has_this", TestContext.Current!.Execution.CancellationToken)).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Sources_can_be_added_and_removed()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();
        var id = await app.CreatePostAsync(24);

        var added = await client.PostAsJsonAsync($"/api/v1/posts/{id}/sources", new { url = "https://example.com/a" }, cancellationToken: TestContext.Current!.Execution.CancellationToken);
        var source = (await added.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current!.Execution.CancellationToken)).GetProperty("sources")[0];

        var removed = await client.DeleteAsync($"/api/v1/posts/{id}/sources/{source.GetProperty("id").GetInt32()}", TestContext.Current!.Execution.CancellationToken);
        await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        var bad = await client.PostAsJsonAsync($"/api/v1/posts/{id}/sources", new { url = "javascript:alert(1)" }, cancellationToken: TestContext.Current!.Execution.CancellationToken);
        await Assert.That(bad.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Tag_autocomplete_answers_json()
    {
        using var app = new TestApp();
        var id = await app.CreatePostAsync(24);
        await app.TagAsync(id, "landscape");

        var tags = await app.CreateNonRedirectingClient().GetFromJsonAsync<JsonElement>("/api/v1/tags?q=land", cancellationToken: TestContext.Current!.Execution.CancellationToken);

        await Assert.That(tags[0].GetProperty("display").GetString()).IsEqualTo("landscape");
    }

    // ---- imports -------------------------------------------------------

    [Test]
    public async Task An_import_is_queued_and_its_status_readable()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();

        var queued = await client.PostAsJsonAsync("/api/v1/imports", new { url = "https://example.com/gallery" }, cancellationToken: TestContext.Current!.Execution.CancellationToken);
        await Assert.That(queued.StatusCode).IsEqualTo(HttpStatusCode.Accepted);

        await app.WaitForJobsAsync();

        var status = await client.GetFromJsonAsync<JsonElement>(queued.Headers.Location!.OriginalString, cancellationToken: TestContext.Current!.Execution.CancellationToken);
        await Assert.That(status.GetProperty("url").GetString()).IsEqualTo("https://example.com/gallery");
        await Assert.That(status.GetProperty("state").GetString()).IsNotEqualTo("Queued");
    }

    [Test]
    public async Task An_import_needs_a_http_url()
    {
        using var app = new TestApp();

        var response = await app.CreateNonRedirectingClient()
            .PostAsJsonAsync("/api/v1/imports", new { url = "file:///etc/passwd" }, cancellationToken: TestContext.Current!.Execution.CancellationToken);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    private static MultipartFormDataContent Upload(byte[] file, string? tags = null, string? source = null)
    {
        var content = new MultipartFormDataContent { { new ByteArrayContent(file), "file", "upload.png" } };
        if (tags is not null) content.Add(new StringContent(tags), "tags");
        if (source is not null) content.Add(new StringContent(source), "source");
        return content;
    }

    /// <summary>A client carrying a fresh token for the seeded administrator.</summary>
    private static async Task<HttpClient> TokenClientAsync(TestApp app)
    {
        string secret;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BohDbContext>();
            var admin = await db.Users.SingleAsync(u => u.Username == UserService.AdminUsername);

            var (_, created) = await scope.ServiceProvider.GetRequiredService<ApiTokenService>()
                .CreateAsync(admin.Id, "test", TestContext.Current!.Execution.CancellationToken);
            secret = created!;
        }

        var client = app.CreateNonRedirectingClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }
}
