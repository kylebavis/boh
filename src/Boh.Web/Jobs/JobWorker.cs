namespace Boh.Web.Jobs;

/// <summary>
/// Runs what <see cref="JobQueue"/> holds: each lane's jobs one at a time, the lanes alongside
/// one another.
/// </summary>
/// <remarks>
/// One job at a time per lane is deliberate. Maintenance passes decode originals and write to
/// the same tables; two at once would fight over the CPU and the SQLite write lock to finish no
/// sooner.
/// </remarks>
public sealed class JobWorker(JobQueue queue, IServiceScopeFactory scopes, ILogger<JobWorker> logger)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enum.GetValues<JobLane>().Select(lane => RunLaneAsync(lane, stoppingToken)));

    private async Task RunLaneAsync(JobLane lane, CancellationToken stopping)
    {
        // Off the host's start-up path first: ExecuteAsync is called synchronously as the host
        // starts, and a lane with work already waiting would otherwise hold that up.
        await Task.Yield();

        try
        {
            await foreach (var id in queue.Reader(lane).ReadAllAsync(stopping))
            {
                if (queue.TryStart(id) is { } job) await RunAsync(job, stopping);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Shutting down. Anything still waiting goes with the process, which the queue's
            // remarks explain is harmless.
        }
    }

    private async Task RunAsync(StartedJob job, CancellationToken stopping)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation, stopping);
        await using var scope = scopes.CreateAsyncScope();

        var context = new JobContext(
            scope.ServiceProvider, cancellation.Token, progress => queue.Report(job.Id, progress));

        try
        {
            queue.Succeed(job.Id, await job.Work(context));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            queue.MarkCancelled(job.Id,
                stopping.IsCancellationRequested ? "Stopped because the server shut down." : null);
        }
        catch (Exception ex)
        {
            // The exception stays in the log: an import's is shown to whoever queued it, and a
            // stack trace or a server path is not something to hand them.
            logger.LogError(ex, "Background job {Kind} failed", job.Kind);
            queue.Fail(job.Id, "Failed with an unexpected error. The server log has the details.");
        }
    }
}
