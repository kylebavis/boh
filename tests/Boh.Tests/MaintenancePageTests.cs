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

    [Fact]
    public async Task An_administrator_reaches_it_from_the_nav()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        var html = await app.GetHtmlAsync(client, "/");

        Assert.Contains("href=\"/Maintenance\"", html);
    }

    [Fact]
    public async Task A_signed_out_visitor_is_not_shown_it()
    {
        using var app = new TestApp(authMode: "password", publicRead: true);

        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), "/");

        Assert.DoesNotContain("href=\"/Maintenance\"", html);
    }

    [Fact]
    public async Task Tag_administration_keeps_only_configuration()
    {
        using var app = new TestApp();
        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), "/Tags/Admin");

        Assert.DoesNotContain("Rebuild implied tags", html);
        Assert.DoesNotContain("Recount tag totals", html);
    }

    [Fact]
    public async Task Every_task_has_a_button_that_starts_it()
    {
        using var app = new TestApp();
        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), Url);

        var actions = Regex.Matches(html, "<form.*?</form>", RegexOptions.Singleline)
            .Select(m => TestApp.FormAction(m.Value))
            .ToList();

        foreach (var task in MaintenanceTask.All)
        {
            Assert.Contains(actions, a => a.Contains("handler=Start") && a.Contains($"task={task.Key}"));
            Assert.Contains(task.Title, html);
        }
    }

    [Fact]
    public async Task Starting_a_task_returns_to_its_section()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();

        var response = await app.SubmitFormAsync(client, Url, "task=tag-counts");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Maintenance#tag-counts", response.Headers.Location!.OriginalString);

        await app.WaitForJobsAsync();
    }

    [Fact]
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

        Assert.Contains("hx-get=\"/Maintenance?handler=Status&amp;task=thumbnails\"", block);
        Assert.Contains("hx-trigger=\"every 2s\"", block);

        release.SetResult();
        await app.WaitForJobsAsync();

        // What the poll receives once the task is done: the result, and nothing that polls again.
        var status = await app.GetHtmlAsync(client, $"{Url}?handler=Status&task=thumbnails");

        Assert.Contains("Every post already has a thumbnail", status);
        Assert.DoesNotContain("hx-trigger", status);
    }

    [Fact]
    public async Task A_running_task_can_be_cancelled_from_the_page()
    {
        using var app = new TestApp();
        var client = app.CreateNonRedirectingClient();

        StartThumbnails(app, async job =>
        {
            await Task.Delay(Timeout.Infinite, job.CancellationToken);
            return null;
        });

        var response = await app.SubmitFormAsync(client, Url, "handler=Cancel");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Maintenance#thumbnails", response.Headers.Location!.OriginalString);

        await app.WaitForJobsAsync();

        Assert.Contains("Cancelled before it finished", await app.GetHtmlAsync(client, Url));
    }

    [Fact]
    public async Task An_unknown_task_is_not_found()
    {
        using var app = new TestApp();

        var response = await app.CreateNonRedirectingClient().GetAsync($"{Url}?handler=Status&task=nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
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

        Assert.Contains("Rebuilt implied tags", html);
        Assert.Equal(1, await WithDbAsync(app, db => db.PostTags
            .CountAsync(pt => pt.PostId == post && pt.Source == TagSource.Implied)));
    }

    [Fact]
    public async Task Rebuilding_an_intact_collection_says_there_was_nothing_to_fix()
    {
        using var app = new TestApp();
        var post = await app.CreatePostAsync(24);
        await app.TagAsync(post, "meme:pondering_my_orb");
        await app.AddImplicationAsync("meme:pondering_my_orb", "format:reaction_image");

        var html = await app.SubmitAndWaitAsync(Url, "task=implied-tags");

        Assert.Contains("already correct", html);
    }

    [Fact]
    public async Task Recounting_repairs_a_total_that_drifted()
    {
        using var app = new TestApp();
        var post = await app.CreatePostAsync(24);
        await app.TagAsync(post, "landscape");

        await WithDbAsync(app, db => db.Tags.ExecuteUpdateAsync(s => s.SetProperty(t => t.PostCount, 99)));

        var html = await app.SubmitAndWaitAsync(Url, "task=tag-counts");

        Assert.Contains("Tag post counts recomputed", html);
        Assert.Equal(1, await WithDbAsync(app, db => db.Tags
            .Where(t => t.Name == "landscape")
            .Select(t => t.PostCount)
            .SingleAsync()));
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
