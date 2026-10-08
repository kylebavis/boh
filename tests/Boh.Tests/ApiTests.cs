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

    [Fact]
    public async Task Without_a_token_a_private_instance_answers_401()
    {
        using var app = new TestApp(authMode: "password");

        var response = await app.CreateNonRedirectingClient().GetAsync("/api/v1/posts");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
    }

    /// <summary>The cookie is refused so a page elsewhere cannot drive the API with a visitor's session.</summary>
    [Fact]
    public async Task A_signed_in_cookie_is_not_enough()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        var response = await client.GetAsync("/api/v1/posts");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_reads_and_writes()
    {
        using var app = new TestApp(authMode: "password");
        var client = await TokenClientAsync(app);
        var id = await app.CreatePostAsync(24);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/posts");
        Assert.Equal(id, list.GetProperty("posts")[0].GetProperty("id").GetInt32());

        var tagged = await client.PostAsJsonAsync($"/api/v1/posts/{id}/tags", new { tags = new[] { "landscape" } });
        Assert.Equal(HttpStatusCode.OK, tagged.StatusCode);
    }

    [Fact]
    public async Task An_unknown_token_is_refused()
    {
        using var app = new TestApp(authMode: "password");
        var client = app.CreateNonRedirectingClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "boh_not-a-real-token");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/posts")).StatusCode);
    }

    [Fact]
    public async Task A_revoked_token_stops_working()
    {
        using var app = new TestApp(authMode: "password");
        var client = await TokenClientAsync(app);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/posts")).StatusCode);

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BohDbContext>();
            await db.ApiTokens.ExecuteDeleteAsync();
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/posts")).StatusCode);
    }

    [Fact]
    public async Task Public_read_opens_reads_but_not_writes()
    {
        using var app = new TestApp(authMode: "password", publicRead: true);
        var client = app.CreateNonRedirectingClient();
        var id = await app.CreatePostAsync(24);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/posts/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync($"/api/v1/posts/{id}")).StatusCode);
    }

    [Fact]
    public async Task With_auth_off_no_token_is_needed()
    {
        using var app = new TestApp();
        var id = await app.CreatePostAsync(24);

        var response = await app.CreateNonRedirectingClient().DeleteAsync($"/api/v1/posts/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ---- the account page ----------------------------------------------

    [Fact]
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
            }));
        var html = await created.Content.ReadAsStringAsync();

        var secret = Regex.Match(html, "id=\"new-token\" value=\"(boh_[^\"]+)\"");
        Assert.True(secret.Success, "the new token was not shown");

        var api = app.CreateNonRedirectingClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret.Groups[1].Value);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/v1/posts")).StatusCode);

        var revoke = TestApp.FormWithField(await app.GetHtmlAsync(browser, "/Account"), "tokenId");
        var revoked = await browser.PostAsync(TestApp.FormAction(revoke), new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["tokenId"] = TestApp.FormValue(revoke, "tokenId"),
                ["__RequestVerificationToken"] = TestApp.FormValue(revoke, "__RequestVerificationToken"),
            }));
        Assert.Contains("Token revoked.", await revoked.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/v1/posts")).StatusCode);
    }

    // ---- posts ---------------------------------------------------------

    [Fact]
    public async Task Uploading_creates_a_tagged_post_with_its_source()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();

        var response = await client.PostAsync("/api/v1/posts",
            Upload(TestEnvironment.MakePng(24, 24), tags: "landscape artist:someone", source: "https://example.com/1"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var post = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(
            ["artist:someone", "landscape"],
            post.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("display").GetString()).Order());
        Assert.Equal("https://example.com/1", post.GetProperty("sources")[0].GetProperty("url").GetString());
        Assert.Equal($"/api/v1/posts/{post.GetProperty("id").GetInt32()}", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Uploading_a_stored_file_answers_409_naming_the_post()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();
        var file = TestEnvironment.MakePng(24, 24);

        var first = await (await client.PostAsync("/api/v1/posts", Upload(file))).Content.ReadFromJsonAsync<JsonElement>();
        var second = await client.PostAsync("/api/v1/posts", Upload(file));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(first.GetProperty("id").GetInt32(), body.GetProperty("postId").GetInt32());
    }

    [Fact]
    public async Task Uploading_without_a_file_is_a_400()
    {
        using var app = new TestApp();

        var response = await app.CreateNonRedirectingClient().PostAsync("/api/v1/posts", new MultipartFormDataContent());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_replaces_the_explicit_tags()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();
        var id = await app.CreatePostAsync(24);
        await app.TagAsync(id, "old", "keep");

        var response = await client.PutAsJsonAsync($"/api/v1/posts/{id}/tags", new { tags = new[] { "keep", "new" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["keep", "new"], (await app.ExplicitTagsAsync(id)).Select(t => t.Display).Order());
    }

    [Fact]
    public async Task Tagging_a_missing_post_is_a_404()
    {
        using var app = new TestApp();

        var response = await app.CreateNonRedirectingClient()
            .PostAsJsonAsync("/api/v1/posts/999/tags", new { tags = new[] { "x" } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Search_and_random_follow_the_query()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();
        var tagged = await app.CreatePostAsync(24);
        await app.CreatePostAsync(32);
        await app.TagAsync(tagged, "landscape");

        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/posts?q=landscape");
        Assert.Equal(1, list.GetProperty("totalCount").GetInt32());

        var random = await client.GetFromJsonAsync<JsonElement>("/api/v1/posts/random?q=landscape");
        Assert.Equal(tagged, random.GetProperty("id").GetInt32());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/posts/random?q=nothing_has_this")).StatusCode);
    }

    [Fact]
    public async Task Sources_can_be_added_and_removed()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();
        var id = await app.CreatePostAsync(24);

        var added = await client.PostAsJsonAsync($"/api/v1/posts/{id}/sources", new { url = "https://example.com/a" });
        var source = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sources")[0];

        var removed = await client.DeleteAsync($"/api/v1/posts/{id}/sources/{source.GetProperty("id").GetInt32()}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        var bad = await client.PostAsJsonAsync($"/api/v1/posts/{id}/sources", new { url = "javascript:alert(1)" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Tag_autocomplete_answers_json()
    {
        using var app = new TestApp();
        var id = await app.CreatePostAsync(24);
        await app.TagAsync(id, "landscape");

        var tags = await app.CreateNonRedirectingClient().GetFromJsonAsync<JsonElement>("/api/v1/tags?q=land");

        Assert.Equal("landscape", tags[0].GetProperty("display").GetString());
    }

    // ---- imports -------------------------------------------------------

    [Fact]
    public async Task An_import_is_queued_and_its_status_readable()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();

        var queued = await client.PostAsJsonAsync("/api/v1/imports", new { url = "https://example.com/gallery" });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);

        await app.WaitForJobsAsync();

        var status = await client.GetFromJsonAsync<JsonElement>(queued.Headers.Location!.OriginalString);
        Assert.Equal("https://example.com/gallery", status.GetProperty("url").GetString());
        Assert.NotEqual("Queued", status.GetProperty("state").GetString());
    }

    [Fact]
    public async Task An_import_needs_a_http_url()
    {
        using var app = new TestApp();

        var response = await app.CreateNonRedirectingClient()
            .PostAsJsonAsync("/api/v1/imports", new { url = "file:///etc/passwd" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
                .CreateAsync(admin.Id, "test", CancellationToken.None);
            secret = created!;
        }

        var client = app.CreateNonRedirectingClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }
}
