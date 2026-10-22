using System.Net;
using System.Text.RegularExpressions;
using Boh.Web.Data;
using Boh.Web.Data.Entities;
using Boh.Web.Jobs;
using Boh.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Boh.Tests;

/// <summary>
/// Maintenance is where every push-button repair lives, reached from the nav rather than through
/// the tag pages, and each runs as a background job. The duplicate actions have page tests of
/// their own; these cover the page being findable, the job round trip — button, progress,
/// result — and the tag repairs.
/// </summary>
public class MaintenancePageTests
{
    private const string Url = "/Maintenance";

    [Test]
    public async Task An_administrator_reaches_it_from_the_nav()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        var html = await app.GetHtmlAsync(client, "/");

        await Assert.That(html).Contains("href=\"/Maintenance\"");
    }

    [Test]
    public async Task A_signed_out_visitor_is_not_shown_it()
    {
        using var app = new TestApp(authMode: "password", publicRead: true);

        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), "/");

        await Assert.That(html).DoesNotContain("href=\"/Maintenance\"");
    }

    [Test]
    public async Task Tag_administration_keeps_only_configuration()
    {
        using var app = new TestApp();
        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), "/Tags/Admin");

        await Assert.That(html).DoesNotContain("Rebuild implied tags");
        await Assert.That(html).DoesNotContain("Recount tag totals");
    }

    [Test]
    public async Task Every_task_has_a_button_that_starts_it()
    {
        using var app = new TestApp();
        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), Url);

        var actions = Regex.Matches(html, "<form.*?</form>", RegexOptions.Singleline)
            .Select(m => TestApp.FormAction(m.Value))
            .ToList();

        foreach (var task in MaintenanceTask.All)
        {
            await Assert.That(actions).Contains(a => a.Contains("handler=Start") && a.Contains($"task={task.Key}"));
            await Assert.That(html).Contains(task.Title);
        }
    }

    [Test]
    public async Task Starting_a_task_returns_to_its_section()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();

        var response = await app.SubmitFormAsync(client, Url, "task=tag-counts");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location!.OriginalString).IsEqualTo("/Maintenance#tag-counts");

        await app.WaitForJobsAsync();
    }

    [Test]
    public async Task A_running_task_polls_for_its_status_until_it_finishes()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        StartThumbnails(app, async _ =>
        {
            await release.Task;
            return new ThumbnailRepairResult(0, 0, 0);
        });

        var html = await app.GetHtmlAsync(client, Url);
        var block = Regex.Match(html, "<div id=\"job-thumbnails\"[^>]*>").Value;

        await Assert.That(block).Contains("hx-get=\"/Maintenance?handler=Status&amp;task=thumbnails\"");
        await Assert.That(block).Contains("hx-trigger=\"every 2s\"");

        release.SetResult();
        await app.WaitForJobsAsync();

        // What the poll receives once the task is done: the result, and nothing that polls again.
        var status = await app.GetHtmlAsync(client, $"{Url}?handler=Status&task=thumbnails");

        await Assert.That(status).Contains("Every post already has a thumbnail");
        await Assert.That(status).DoesNotContain("hx-trigger");
    }

    [Test]
    public async Task A_running_task_can_be_canceled_from_the_page()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();

        StartThumbnails(app, async job =>
        {
            await Task.Delay(Timeout.Infinite, job.CancellationToken);
            return null;
        });

        var response = await app.SubmitFormAsync(client, Url, "handler=Cancel");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location!.OriginalString).IsEqualTo("/Maintenance#thumbnails");

        await app.WaitForJobsAsync();

        await Assert.That(await app.GetHtmlAsync(client, Url)).Contains("Canceled before it finished");
    }

    [Test]
    public async Task An_unknown_task_is_not_found()
    {
        using var app = new TestApp();

        var response = await app.CreateNonRedirectingClient().GetAsync($"{Url}?handler=Status&task=nope", TestContext.Current!.Execution.CancellationToken);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Rebuilding_restores_an_implied_tag_that_went_missing()
    {
        using var app = new TestApp();
        var post = await app.CreatePostAsync(24);
        await app.TagAsync(post, "meme:pondering_my_orb");
        await app.AddImplicationAsync("meme:pondering_my_orb", "format:reaction_image");

        await WithDbAsync(app, db => db.PostTags
            .Where(pt => pt.Source == TagSource.Implied)
            .ExecuteDeleteAsync());

        var html = await app.SubmitAndWaitAsync(Url, "task=implied-tags");

        await Assert.That(html).Contains("Rebuilt implied tags");
        await Assert.That(await WithDbAsync(app, db => db.PostTags
            .CountAsync(pt => pt.PostId == post && pt.Source == TagSource.Implied))).IsEqualTo(1);
    }

    [Test]
    public async Task Rebuilding_an_intact_collection_says_there_was_nothing_to_fix()
    {
        using var app = new TestApp();
        var post = await app.CreatePostAsync(24);
        await app.TagAsync(post, "meme:pondering_my_orb");
        await app.AddImplicationAsync("meme:pondering_my_orb", "format:reaction_image");

        var html = await app.SubmitAndWaitAsync(Url, "task=implied-tags");

        await Assert.That(html).Contains("already correct");
    }

    [Test]
    public async Task Recounting_repairs_a_total_that_drifted()
    {
        using var app = new TestApp();
        var post = await app.CreatePostAsync(24);
        await app.TagAsync(post, "landscape");

        await WithDbAsync(app, db => db.Tags.ExecuteUpdateAsync(s => s.SetProperty(t => t.PostCount, 99)));

        var html = await app.SubmitAndWaitAsync(Url, "task=tag-counts");

        await Assert.That(html).Contains("Tag post counts recomputed");
        await Assert.That(await WithDbAsync(app, db => db.Tags
            .Where(t => t.Name == "landscape")
            .Select(t => t.PostCount)
            .SingleAsync())).IsEqualTo(1);
    }

    /// <summary>
    /// Queues work under the thumbnail task's kind, so the page renders it as that task — which
    /// is how a test holds a task mid-run long enough to look at it.
    /// </summary>
    private static void StartThumbnails(TestApp app, JobWork work) =>
        app.Jobs.Enqueue(JobLane.Maintenance, MaintenanceTask.Thumbnails.Kind, MaintenanceTask.Thumbnails.Title,
            null, work, exclusive: true);

    private static async Task<T> WithDbAsync<T>(TestApp app, Func<BohDbContext, Task<T>> action)
    {
        using var scope = app.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<BohDbContext>());
    }
}
