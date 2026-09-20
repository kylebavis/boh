using System.Net;
using System.Text.RegularExpressions;
using Boh.Web.Data;
using Boh.Web.Jobs;
using Boh.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Boh.Tests;

/// <summary>
/// The one page posts come in through. Two forms share a page model, so part of what these
/// guard is each form reaching its own handler; the rest is the URL form's round trip through
/// the job queue, and that one person's imports stay theirs.
/// </summary>
public class ImportPageTests
{
    private const string Url = "/Import";

    [Fact]
    public async Task Each_form_submits_to_its_own_handler()
    {
        using var app = new TestApp();
        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), Url);

        var upload = TestApp.FormWithField(html, "UploadedFile");
        Assert.Equal("/Import?handler=Upload", TestApp.FormAction(upload));

        // Without it the browser sends the filename and no bytes.
        Assert.Contains("enctype=\"multipart/form-data\"", upload);

        var fetch = TestApp.FormWithField(html, "SourceUrl");
        Assert.Equal("/Import?handler=Url", TestApp.FormAction(fetch));
    }

    [Fact]
    public async Task Uploading_a_file_opens_the_new_post()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();

        var response = await UploadAsync(app, client, TestEnvironment.MakePng(24, 24));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Matches(@"^/Posts/Detail/\d+$", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Uploading_a_stored_file_again_links_to_the_original()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();
        var file = TestEnvironment.MakePng(24, 24);

        var first = await UploadAsync(app, client, file);
        var original = first.Headers.Location!.OriginalString;

        var second = await UploadAsync(app, client, file);
        var html = await second.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("already stored as", html);
        Assert.Contains($"href=\"{original}\"", html);
    }

    /// <summary>
    /// The standalone upload page recorded no uploader, so a user's post count on the Users page
    /// only ever reflected their imports.
    /// </summary>
    [Fact]
    public async Task An_upload_is_credited_to_the_account_that_made_it()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        var response = await UploadAsync(app, client, TestEnvironment.MakePng(24, 24));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BohDbContext>();

        var admin = await db.Users.SingleAsync(u => u.Username == UserService.AdminUsername);
        var post = await db.Posts.SingleAsync();
        Assert.Equal(admin.Id, post.UploadedById);
    }

    [Fact]
    public async Task The_nav_offers_a_single_way_in()
    {
        using var app = new TestApp();

        // An empty gallery adds its own call to action pointing at the same page.
        await app.CreatePostAsync(24);

        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), "/");

        Assert.Single(Regex.Matches(html, "href=\"/Import\""));
        Assert.DoesNotContain("/Posts/Upload", html);
    }

    [Fact]
    public async Task A_url_is_queued_and_its_outcome_listed_under_the_form()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();

        var response = await SubmitUrlAsync(app, client, "https://example.com/gallery");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Import#imports", response.Headers.Location!.OriginalString);

        await app.WaitForJobsAsync();
        var html = await app.GetHtmlAsync(client, Url);

        // Nothing is fetchable there, and the test machine need not have gallery-dl at all, so
        // the outcome is a reported failure — which is enough: a job ran, and what it said
        // reached the page.
        Assert.Contains("Recent imports", html);
        Assert.Contains("https://example.com/gallery", html);
        Assert.Contains("notice notice-error", html);
    }

    /// <summary>
    /// The skipped row names the post that already holds those bytes, and does it as a link.
    /// Printing the id on its own left the reader to go and find it by hand, which is the
    /// one thing they want to do next.
    /// </summary>
    [Fact]
    public async Task A_file_the_collection_already_holds_links_to_the_post_that_holds_it()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();

        var postId = await app.CreatePostAsync(24);

        app.Jobs.Enqueue(JobLane.Import, GalleryDlImporter.JobKind, "https://example.com/gallery", null,
            _ => Task.FromResult<object?>(new ImportResult(
                [],
                [new SkippedItem("cat.jpg", "already stored as", postId)],
                null)));

        await app.WaitForJobsAsync();
        var html = await app.GetHtmlAsync(client, Url);

        Assert.Contains("already stored as", html);
        Assert.Contains($"<a href=\"/Posts/Detail/{postId}\">post {postId}</a>", html);
    }

    /// <summary>A skip that is not a duplicate has no post to point at, and gets no link.</summary>
    [Fact]
    public async Task A_file_skipped_for_any_other_reason_is_reported_as_plain_text()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();

        app.Jobs.Enqueue(JobLane.Import, GalleryDlImporter.JobKind, "https://example.com/gallery", null,
            _ => Task.FromResult<object?>(new ImportResult(
                [],
                [new SkippedItem("notes.txt", "that file type is not accepted")],
                null)));

        await app.WaitForJobsAsync();
        var html = await app.GetHtmlAsync(client, Url);

        Assert.Contains("that file type is not accepted", html);
        Assert.DoesNotContain("/Posts/Detail/", html);
    }

    [Fact]
    public async Task A_blank_address_is_refused_without_queueing_anything()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();

        var response = await SubmitUrlAsync(app, client, "");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Enter a URL to import.", html);
        Assert.Empty(app.Jobs.List());
    }

    [Fact]
    public async Task A_running_import_polls_for_progress_and_can_be_cancelled()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();

        var job = app.Jobs.Enqueue(JobLane.Import, GalleryDlImporter.JobKind, "https://example.com/slow", null,
            async context =>
            {
                await Task.Delay(Timeout.Infinite, context.CancellationToken);
                return null;
            });

        var html = await app.GetHtmlAsync(client, Url);
        var block = Regex.Match(html, $"<div id=\"import-{job.Id}\"[^>]*>").Value;
        Assert.Contains("hx-trigger=\"every 2s\"", block);

        var response = await app.SubmitFormAsync(client, Url, $"id={job.Id}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        await app.WaitForJobsAsync();

        Assert.Equal(JobState.Cancelled, app.Jobs.Get(job.Id)!.State);
        Assert.Contains("Anything stored before then was kept", await app.GetHtmlAsync(client, Url));
    }

    [Fact]
    public async Task Someone_elses_import_is_neither_listed_nor_shown()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        var theirs = app.Jobs.Enqueue(JobLane.Import, GalleryDlImporter.JobKind, "https://example.com/theirs",
            requestedById: 9999, _ => Task.FromResult<object?>(new ImportResult([], [], null)));
        await app.WaitForJobsAsync();

        Assert.DoesNotContain("example.com/theirs", await app.GetHtmlAsync(client, Url));

        // Nothing rather than an error, the same as for an import a restart forgot, so a poll
        // for it removes the block instead of retrying forever.
        var fragment = await client.GetAsync($"{Url}?handler=Job&id={theirs.Id}");
        Assert.Equal(HttpStatusCode.OK, fragment.StatusCode);
        Assert.Equal("", await fragment.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Someone_elses_import_cannot_be_cancelled()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();

        var theirs = app.Jobs.Enqueue(JobLane.Import, GalleryDlImporter.JobKind, "https://example.com/theirs",
            requestedById: 9999, async context =>
            {
                await Task.Delay(Timeout.Infinite, context.CancellationToken);
                return null;
            });

        // The token comes off this person's own page; the id is someone else's.
        var page = await app.GetHtmlAsync(client, Url);
        var token = TestApp.FormValue(TestApp.FormWithField(page, "SourceUrl"), "__RequestVerificationToken");

        var response = await client.PostAsync($"{Url}?handler=Cancel&id={theirs.Id}",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(JobState.Running, app.Jobs.Get(theirs.Id)!.State);

        app.Jobs.Cancel(theirs.Id);
        await app.WaitForJobsAsync();
    }

    private static async Task<HttpResponseMessage> UploadAsync(TestApp app, HttpClient client, byte[] file)
    {
        var page = await app.GetHtmlAsync(client, Url);
        var form = TestApp.FormWithField(page, "UploadedFile");

        using var content = new MultipartFormDataContent
        {
            { new StringContent(TestApp.FormValue(form, "__RequestVerificationToken")), "__RequestVerificationToken" },
            { new ByteArrayContent(file), "UploadedFile", "upload.png" },
        };

        return await client.PostAsync(TestApp.FormAction(form), content);
    }

    private static async Task<HttpResponseMessage> SubmitUrlAsync(TestApp app, HttpClient client, string url)
    {
        var page = await app.GetHtmlAsync(client, Url);
        var form = TestApp.FormWithField(page, "SourceUrl");

        return await client.PostAsync(TestApp.FormAction(form), new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = TestApp.FormValue(form, "__RequestVerificationToken"),
                ["SourceUrl"] = url,
            }));
    }
}
