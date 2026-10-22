using Boh.Web.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Boh.Tests;

/// <summary>
/// The queue and its worker without the web app around them: what runs when, what a failed or
/// canceled job leaves behind, and that one busy lane does not stall the other.
/// </summary>
public class JobQueueTests
{
    [Test]
    public async Task A_job_runs_and_keeps_its_result()
    {
        await using var jobs = await Harness.StartAsync();

        var job = jobs.Queue.Enqueue(JobLane.Maintenance, "test", "Test", null, Returns(42));
        var done = await jobs.WaitAsync(job.Id, j => !j.IsActive);

        await Assert.That(done.State).IsEqualTo(JobState.Succeeded);
        await Assert.That(done.Result).IsEqualTo(42);
        await Assert.That(done.FinishedAt).IsNotNull();
    }

    [Test]
    public async Task Progress_is_visible_while_the_job_runs()
    {
        await using var jobs = await Harness.StartAsync();
        var release = Gate();

        var job = jobs.Queue.Enqueue(JobLane.Maintenance, "test", "Test", null, async context =>
        {
            context.Report(new JobProgress("Working", 3, 10));
            await release.Task;
            return null;
        });

        var running = await jobs.WaitAsync(job.Id, j => j.Progress is not null);

        await Assert.That(running.State).IsEqualTo(JobState.Running);
        await Assert.That(running.Progress).IsEqualTo(new JobProgress("Working", 3, 10));

        release.SetResult();
        await jobs.WaitAsync(job.Id, j => !j.IsActive);
    }

    [Test]
    public async Task A_job_that_throws_is_marked_failed_and_the_lane_carries_on()
    {
        await using var jobs = await Harness.StartAsync();

        var broken = jobs.Queue.Enqueue(JobLane.Maintenance, "broken", "Broken", null,
            _ => throw new InvalidOperationException("/srv/secret/path is unreadable"));
        var next = jobs.Queue.Enqueue(JobLane.Maintenance, "next", "Next", null, Returns("ok"));

        var failed = await jobs.WaitAsync(broken.Id, j => !j.IsActive);
        await Assert.That(failed.State).IsEqualTo(JobState.Failed);

        // What went wrong goes to the log; the page gets a message that leaks nothing.
        await Assert.That(failed.Message).DoesNotContain("/srv/secret");

        await Assert.That((await jobs.WaitAsync(next.Id, j => !j.IsActive)).State).IsEqualTo(JobState.Succeeded);
    }

    [Test]
    public async Task Jobs_in_one_lane_run_one_at_a_time()
    {
        await using var jobs = await Harness.StartAsync();
        var release = Gate();

        var first = jobs.Queue.Enqueue(JobLane.Maintenance, "first", "First", null, Blocks(release));
        var second = jobs.Queue.Enqueue(JobLane.Maintenance, "second", "Second", null, Returns(null));

        await jobs.WaitAsync(first.Id, j => j.State == JobState.Running);
        await Task.Delay(100, TestContext.Current!.Execution.CancellationToken);
        await Assert.That(jobs.Queue.Get(second.Id)!.State).IsEqualTo(JobState.Queued);

        release.SetResult();
        await Assert.That((await jobs.WaitAsync(second.Id, j => !j.IsActive)).State).IsEqualTo(JobState.Succeeded);
    }

    [Test]
    public async Task A_busy_lane_does_not_hold_up_the_other()
    {
        await using var jobs = await Harness.StartAsync();
        var release = Gate();

        var backfill = jobs.Queue.Enqueue(JobLane.Maintenance, "hashes", "Hashes", null, Blocks(release));
        await jobs.WaitAsync(backfill.Id, j => j.State == JobState.Running);

        var import = jobs.Queue.Enqueue(JobLane.Import, "import", "https://example.com/", null, Returns(null));

        await Assert.That((await jobs.WaitAsync(import.Id, j => !j.IsActive)).State).IsEqualTo(JobState.Succeeded);
        await Assert.That(jobs.Queue.Get(backfill.Id)!.State).IsEqualTo(JobState.Running);

        release.SetResult();
    }

    [Test]
    public async Task Cancelling_a_running_job_stops_it()
    {
        await using var jobs = await Harness.StartAsync();

        var job = jobs.Queue.Enqueue(JobLane.Maintenance, "test", "Test", null, RunsUntilCanceled);
        await jobs.WaitAsync(job.Id, j => j.State == JobState.Running);

        await Assert.That(jobs.Queue.Cancel(job.Id)).IsTrue();

        var stopped = await jobs.WaitAsync(job.Id, j => !j.IsActive);
        await Assert.That(stopped.State).IsEqualTo(JobState.Canceled);
        await Assert.That(stopped.Message).IsNull();
    }

    [Test]
    public async Task A_job_canceled_while_waiting_never_runs()
    {
        await using var jobs = await Harness.StartAsync();
        var release = Gate();
        var ran = false;

        var ahead = jobs.Queue.Enqueue(JobLane.Maintenance, "ahead", "Ahead", null, Blocks(release));
        await jobs.WaitAsync(ahead.Id, j => j.State == JobState.Running);

        var waiting = jobs.Queue.Enqueue(JobLane.Maintenance, "waiting", "Waiting", null, _ =>
        {
            ran = true;
            return Task.FromResult<object?>(null);
        });

        await Assert.That(jobs.Queue.Cancel(waiting.Id)).IsTrue();
        await Assert.That(jobs.Queue.Get(waiting.Id)!.State).IsEqualTo(JobState.Canceled);

        release.SetResult();

        // Queued behind the canceled one, so by the time this finishes the lane has been past it.
        var behind = jobs.Queue.Enqueue(JobLane.Maintenance, "behind", "Behind", null, Returns(null));
        await jobs.WaitAsync(behind.Id, j => !j.IsActive);

        await Assert.That(ran).IsFalse();
    }

    [Test]
    public async Task An_exclusive_kind_is_not_queued_twice()
    {
        await using var jobs = await Harness.StartAsync();
        var release = Gate();

        var first = jobs.Queue.Enqueue(JobLane.Maintenance, "scan", "Scan", null, Blocks(release), exclusive: true);
        var again = jobs.Queue.Enqueue(JobLane.Maintenance, "scan", "Scan", null, Returns(null), exclusive: true);

        await Assert.That(again.Id).IsEqualTo(first.Id);
        await Assert.That(jobs.Queue.List(j => j.Kind == "scan")).HasSingleItem();

        release.SetResult();
        await jobs.WaitAsync(first.Id, j => !j.IsActive);

        // Once it has finished, the button starts a fresh run.
        var later = jobs.Queue.Enqueue(JobLane.Maintenance, "scan", "Scan", null, Returns(null), exclusive: true);
        await Assert.That(later.Id).IsNotEqualTo(first.Id);
    }

    [Test]
    public async Task Only_the_most_recent_finished_runs_are_remembered()
    {
        await using var jobs = await Harness.StartAsync();
        var runs = JobQueue.FinishedKeptPerKind + 5;

        var last = Guid.Empty;
        for (var i = 0; i < runs; i++)
        {
            last = jobs.Queue.Enqueue(JobLane.Import, "import", $"#{i}", null, Returns(i)).Id;
        }

        await jobs.WaitAsync(last, j => !j.IsActive);

        var kept = jobs.Queue.List(j => j.Kind == "import");
        await Assert.That(kept.Count).IsEqualTo(JobQueue.FinishedKeptPerKind);
        await Assert.That(kept[0].Title).IsEqualTo($"#{runs - 1}");
    }

    [Test]
    public async Task Shutting_down_cancels_the_running_job_and_says_why()
    {
        await using var jobs = await Harness.StartAsync();

        var job = jobs.Queue.Enqueue(JobLane.Maintenance, "test", "Test", null, RunsUntilCanceled);
        await jobs.WaitAsync(job.Id, j => j.State == JobState.Running);

        await jobs.StopAsync();

        var stopped = jobs.Queue.Get(job.Id)!;
        await Assert.That(stopped.State).IsEqualTo(JobState.Canceled);
        await Assert.That(stopped.Message!).Contains("shut down");
    }

    private static JobWork Returns(object? value) => _ => Task.FromResult(value);

    private static JobWork Blocks(TaskCompletionSource release) => async _ =>
    {
        await release.Task;
        return null;
    };

    private static async Task<object?> RunsUntilCanceled(JobContext job)
    {
        await Task.Delay(Timeout.Infinite, job.CancellationToken);
        return null;
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly JobWorker _worker;

        private Harness()
        {
            var services = new ServiceCollection().BuildServiceProvider();
            _worker = new JobWorker(
                Queue, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<JobWorker>.Instance);
        }

        public JobQueue Queue { get; } = new();

        public static async Task<Harness> StartAsync()
        {
            var harness = new Harness();
            await harness._worker.StartAsync(TestContext.Current!.Execution.CancellationToken);
            return harness;
        }

        public Task StopAsync() => _worker.StopAsync(CancellationToken.None);

        /// <summary>Polls until the job matches, failing the test rather than hanging it.</summary>
        public async Task<JobSnapshot> WaitAsync(Guid id, Func<JobSnapshot, bool> until)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

            while (true)
            {
                var job = Queue.Get(id);
                await Assert.That(job).IsNotNull();
                if (until(job)) return job;

                await Assert.That(DateTime.UtcNow < deadline).IsTrue().Because($"job {job.Title} is still {job.State}");
                await Task.Delay(10);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _worker.Dispose();
        }
    }
}
