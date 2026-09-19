using System.Threading.Channels;

namespace Boh.Web.Jobs;

/// <summary>
/// Which worker a job waits for. Each lane runs its jobs one at a time and the lanes run
/// alongside each other, so an hour-long hashing pass never holds up an import.
/// </summary>
public enum JobLane
{
    Maintenance,
    Import
}

public enum JobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

/// <summary>
/// How far a running job has got. <paramref name="Total"/> is null when the work cannot be
/// measured in advance — a download, or one large query — which renders as a bar with no end.
/// </summary>
public sealed record JobProgress(string Stage, long Done = 0, long? Total = null);

/// <summary>A job as it stood at one moment, safe to hand to a page.</summary>
/// <param name="Title">What the page calls it: a task name, or the URL being imported.</param>
/// <param name="Message">Why a failed or cancelled job ended, when there is more to say than that it did.</param>
public sealed record JobSnapshot(
    Guid Id,
    JobLane Lane,
    string Kind,
    string Title,
    int? RequestedById,
    JobState State,
    JobProgress? Progress,
    object? Result,
    string? Message,
    DateTimeOffset QueuedAt,
    DateTimeOffset? FinishedAt)
{
    public bool IsActive => State is JobState.Queued or JobState.Running;
}

/// <summary>
/// What a job's work is handed: services from a scope of its own, the token that cancels it,
/// and somewhere to report progress — which is why it can be passed straight to a service as
/// its <see cref="IProgress{T}"/>.
/// </summary>
public sealed class JobContext(
    IServiceProvider services,
    CancellationToken cancellationToken,
    Action<JobProgress> report) : IProgress<JobProgress>
{
    public IServiceProvider Services { get; } = services;
    public CancellationToken CancellationToken { get; } = cancellationToken;

    public void Report(JobProgress value) => report(value);
}

public delegate Task<object?> JobWork(JobContext job);

/// <summary>A job the worker has just moved to running.</summary>
internal sealed record StartedJob(Guid Id, string Kind, JobWork Work, CancellationToken Cancellation);

/// <summary>
/// Work too long for a request — passes over the whole archive, and imports — waiting for
/// <see cref="JobWorker"/> to run it, plus the outcome of what already has.
/// </summary>
/// <remarks>
/// Held in memory only, and every job here is safe to lose that way. A maintenance pass works
/// from whatever is still unrepaired, so running it again after a restart carries on where it
/// stopped; an interrupted import keeps the posts it had already stored. Durable storage would
/// add a second writer to the SQLite file to protect nothing.
/// </remarks>
public sealed class JobQueue
{
    /// <summary>
    /// Finished runs remembered per kind. Enough for a list of recent imports; anything older
    /// describes a collection that has since moved on.
    /// </summary>
    public const int FinishedKeptPerKind = 20;

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _jobs = [];
    private long _sequence;

    private readonly Dictionary<JobLane, Channel<Guid>> _lanes = Enum.GetValues<JobLane>()
        .ToDictionary(
            lane => lane,
            _ => Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true }));

    /// <param name="exclusive">
    /// Allows at most one job of <paramref name="kind"/> queued or running at a time: while there
    /// is one, this returns it rather than adding a second pass over the same rows.
    /// </param>
    public JobSnapshot Enqueue(
        JobLane lane, string kind, string title, int? requestedById, JobWork work, bool exclusive = false)
    {
        lock (_gate)
        {
            if (exclusive && _jobs.Values.FirstOrDefault(j => j.Kind == kind && j.IsActive) is { } existing)
            {
                return existing.Snapshot();
            }

            var entry = new Entry(Guid.NewGuid(), ++_sequence, lane, kind, title, requestedById, work);
            _jobs.Add(entry.Id, entry);

            // Unbounded, so the write cannot be refused.
            _lanes[lane].Writer.TryWrite(entry.Id);

            return entry.Snapshot();
        }
    }

    public JobSnapshot? Get(Guid id)
    {
        lock (_gate)
        {
            return _jobs.GetValueOrDefault(id)?.Snapshot();
        }
    }

    /// <summary>Jobs still remembered, newest first.</summary>
    public IReadOnlyList<JobSnapshot> List(Func<JobSnapshot, bool>? filter = null)
    {
        lock (_gate)
        {
            return
            [
                .. _jobs.Values
                    .OrderByDescending(j => j.Sequence)
                    .Select(j => j.Snapshot())
                    .Where(j => filter is null || filter(j))
            ];
        }
    }

    /// <summary>The most recent job of a kind, finished or not.</summary>
    public JobSnapshot? Latest(string kind) => List(j => j.Kind == kind).FirstOrDefault();

    /// <summary>
    /// Stops a job. One still waiting never starts; a running one is signalled, and ends at its
    /// next cancellation check — so it can show as running for a moment after this returns.
    /// </summary>
    /// <returns>False when there is no such job, or it had already finished.</returns>
    public bool Cancel(Guid id)
    {
        CancellationTokenSource signal;

        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var entry) || !entry.IsActive) return false;

            if (entry.State == JobState.Queued)
            {
                Finish(entry, JobState.Cancelled, null, null);
                return true;
            }

            signal = entry.Cancellation;
        }

        // Outside the lock: cancelling runs the token's callbacks inline, and nothing they do
        // should be able to deadlock against a page reading the queue.
        signal.Cancel();
        return true;
    }

    internal ChannelReader<Guid> Reader(JobLane lane) => _lanes[lane].Reader;

    /// <summary>Moves a waiting job to running, or returns null when it was cancelled while it waited.</summary>
    internal StartedJob? TryStart(Guid id)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var entry) || entry.State != JobState.Queued) return null;

            entry.State = JobState.Running;
            return new StartedJob(entry.Id, entry.Kind, entry.Work, entry.Cancellation.Token);
        }
    }

    internal void Report(Guid id, JobProgress progress)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(id, out var entry) && entry.State == JobState.Running) entry.Progress = progress;
        }
    }

    internal void Succeed(Guid id, object? result) => Finish(id, JobState.Succeeded, result, null);

    internal void Fail(Guid id, string message) => Finish(id, JobState.Failed, null, message);

    internal void MarkCancelled(Guid id, string? message) => Finish(id, JobState.Cancelled, null, message);

    private void Finish(Guid id, JobState state, object? result, string? message)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(id, out var entry)) Finish(entry, state, result, message);
        }
    }

    private void Finish(Entry entry, JobState state, object? result, string? message)
    {
        entry.State = state;
        entry.Result = result;
        entry.Message = message;
        entry.FinishedAt = DateTimeOffset.UtcNow;

        var forgotten = _jobs.Values
            .Where(j => j.Kind == entry.Kind && !j.IsActive)
            .OrderByDescending(j => j.Sequence)
            .Skip(FinishedKeptPerKind)
            .Select(j => j.Id)
            .ToList();

        foreach (var old in forgotten) _jobs.Remove(old);
    }

    /// <summary>The mutable record behind a snapshot. Only ever touched under the queue's lock.</summary>
    private sealed class Entry(
        Guid id, long sequence, JobLane lane, string kind, string title, int? requestedById, JobWork work)
    {
        public Guid Id { get; } = id;
        public long Sequence { get; } = sequence;
        public string Kind { get; } = kind;
        public JobWork Work { get; } = work;
        public CancellationTokenSource Cancellation { get; } = new();

        public JobState State { get; set; } = JobState.Queued;
        public JobProgress? Progress { get; set; }
        public object? Result { get; set; }
        public string? Message { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }

        private readonly DateTimeOffset _queuedAt = DateTimeOffset.UtcNow;

        public bool IsActive => State is JobState.Queued or JobState.Running;

        public JobSnapshot Snapshot() => new(
            Id, lane, Kind, title, requestedById, State, Progress, Result, Message, _queuedAt, FinishedAt);
    }
}
