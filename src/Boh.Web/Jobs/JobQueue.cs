using System.Threading.Channels;

namespace Boh.Web.Jobs;

/// <summary>Each lane runs one job at a time; lanes run in parallel.</summary>
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
    Canceled
}

/// <summary>Null <paramref name="Total"/> means unmeasurable.</summary>
public sealed record JobProgress(string Stage, long Done = 0, long? Total = null);

/// <summary>A job as it stood at one moment, safe to hand to a page.</summary>
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

/// <summary>Scoped services, cancellation, and progress reporting for a job.</summary>
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
/// Background work and recent outcomes. In memory only: every job is safe to rerun after a restart.
/// </summary>
public sealed class JobQueue
{
    public const int FinishedKeptPerKind = 20;

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _jobs = [];
    private long _sequence;

    private readonly Dictionary<JobLane, Channel<Guid>> _lanes = Enum.GetValues<JobLane>()
        .ToDictionary(
            lane => lane,
            _ => Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true }));

    /// <param name="exclusive">At most one active job of this kind; returns the existing one.</param>
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

    /// <summary>Stops a job. A running one ends at its next cancellation check.</summary>
    /// <returns>False when there is no such job, or it had already finished.</returns>
    public bool Cancel(Guid id)
    {
        CancellationTokenSource signal;

        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var entry) || !entry.IsActive) return false;

            if (entry.State == JobState.Queued)
            {
                Finish(entry, JobState.Canceled, null, null);
                return true;
            }

            signal = entry.Cancellation;
        }

        // Outside the lock: cancellation callbacks run inline.
        signal.Cancel();
        return true;
    }

    internal ChannelReader<Guid> Reader(JobLane lane) => _lanes[lane].Reader;

    /// <summary>Moves a waiting job to running, or returns null when it was canceled while it waited.</summary>
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

    internal void MarkCanceled(Guid id, string? message) => Finish(id, JobState.Canceled, null, message);

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
