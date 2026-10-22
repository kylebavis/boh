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

    [Test]
    public async Task Each_form_submits_to_its_own_handler()
    {
        using var app = new TestApp();
        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), Url);

        var upload = TestApp.FormWithField(html, "UploadedFile");
        await Assert.That(TestApp.FormAction(upload)).IsEqualTo("/Import?handler=Upload");

        // Without it the browser sends the filename and no bytes.
        await Assert.That(upload).Contains("enctype=\"multipart/form-data\"");

        var fetch = TestApp.FormWithField(html, "SourceUrl");
        await Assert.That(TestApp.FormAction(fetch)).IsEqualTo("/Import?handler=Url");
    }

    [Test]
    public async Task Uploading_a_file_opens_the_new_post()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();

        var response = await UploadAsync(app, client, TestEnvironment.MakePng(24, 24));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location!.OriginalString).Matches(@"^/Posts/Detail/\d+$");
    }

    [Test]
    public async Task Uploading_a_stored_file_again_links_to_the_original()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();
        var file = TestEnvironment.MakePng(24, 24);

        var first = await UploadAsync(app, client, file);
        var original = first.Headers.Location!.OriginalString;

        var second = await UploadAsync(app, client, file);
        var html = await second.Content.ReadAsStringAsync(TestContext.Current!.Execution.CancellationToken);

        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(html).Contains("already stored as");
        await Assert.That(html).Contains($"href=\"{original}\"");
    }

    /// <summary>
    /// The standalone upload page recorded no uploader, so a user's post count on the Users page
    /// only ever reflected their imports.
    /// </summary>
    [Test]
    public async Task An_upload_is_credited_to_the_account_that_made_it()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        var response = await UploadAsync(app, client, TestEnvironment.MakePng(24, 24));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BohDbContext>();

        var admin = await db.Users.SingleAsync(u => u.Username == UserService.AdminUsername, cancellationToken: TestContext.Current!.Execution.CancellationToken);
        var post = await db.Posts.SingleAsync(cancellationToken: TestContext.Current!.Execution.CancellationToken);
        await Assert.That(post.UploadedById).IsEqualTo(admin.Id);
    }

    [Test]
    public async Task The_nav_offers_a_single_way_in()
    {
        using var app = new TestApp();

        // An empty gallery adds its own call to action pointing at the same page.
        await app.CreatePostAsync(24);

        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), "/");

        await Assert.That(Regex.Matches(html, "href=\"/Import\"")).HasSingleItem();
        await Assert.That(html).DoesNotContain("/Posts/Upload");
    }

    [Test]
    public async Task A_url_is_queued_and_its_outcome_listed_under_the_form()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();

        var response = await SubmitUrlAsync(app, client, "https://example.com/gallery");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location!.OriginalString).IsEqualTo("/Import#imports");

        await app.WaitForJobsAsync();
        var html = await app.GetHtmlAsync(client, Url);

        // Nothing is fetchable there, and the test machine need not have gallery-dl at all, so
        // the outcome is a reported failure — which is enough: a job ran, and what it said
        // reached the page.
        await Assert.That(html).Contains("Recent imports");
        await Assert.That(html).Contains("https://example.com/gallery");
        await Assert.That(html).Contains("notice notice-error");
    }

    /// <summary>
    /// The skipped row names the post that already holds those bytes, and does it as a link.
    /// Printing the id on its own left the reader to go and find it by hand, which is the
    /// one thing they want to do next.
    /// </summary>
    [Test]
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

        await Assert.That(html).Contains("already stored as");
        await Assert.That(html).Contains($"<a href=\"/Posts/Detail/{postId}\">post {postId}</a>");
    }

    /// <summary>A skip that is not a duplicate has no post to point at, and gets no link.</summary>
    [Test]
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

        await Assert.That(html).Contains("that file type is not accepted");
        await Assert.That(html).DoesNotContain("/Posts/Detail/");
    }

    [Test]
    public async Task A_blank_address_is_refused_without_queueing_anything()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();

        var response = await SubmitUrlAsync(app, client, "");
        var html = await response.Content.ReadAsStringAsync(TestContext.Current!.Execution.CancellationToken);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(html).Contains("Enter a URL to import.");
        await Assert.That(app.Jobs.List()).IsEmpty();
    }

    [Test]
    public async Task A_running_import_polls_for_progress_and_can_be_canceled()
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
        await Assert.That(block).Contains("hx-trigger=\"every 2s\"");

        var response = await app.SubmitFormAsync(client, Url, $"id={job.Id}");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);

        await app.WaitForJobsAsync();

        await Assert.That(app.Jobs.Get(job.Id)!.State).IsEqualTo(JobState.Canceled);
        await Assert.That(await app.GetHtmlAsync(client, Url)).Contains("Anything stored before then was kept");
    }

    [Test]
    public async Task Someone_elses_import_is_neither_listed_nor_shown()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        var theirs = app.Jobs.Enqueue(JobLane.Import, GalleryDlImporter.JobKind, "https://example.com/theirs",
            requestedById: 9999, _ => Task.FromResult<object?>(new ImportResult([], [], null)));
        await app.WaitForJobsAsync();

        await Assert.That(await app.GetHtmlAsync(client, Url)).DoesNotContain("example.com/theirs");

        // Nothing rather than an error, the same as for an import a restart forgot, so a poll
        // for it removes the block instead of retrying forever.
        var fragment = await client.GetAsync($"{Url}?handler=Job&id={theirs.Id}", TestContext.Current!.Execution.CancellationToken);
        await Assert.That(fragment.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await fragment.Content.ReadAsStringAsync(TestContext.Current!.Execution.CancellationToken)).IsEqualTo("");
    }

    [Test]
    public async Task Someone_elses_import_cannot_be_canceled()
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
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }),
            TestContext.Current!.Execution.CancellationToken);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(app.Jobs.Get(theirs.Id)!.State).IsEqualTo(JobState.Running);

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
