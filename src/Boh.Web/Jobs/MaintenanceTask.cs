using Boh.Web.Services;

namespace Boh.Web.Jobs;

/// <summary>What rebuilding implied tags reports.</summary>
public sealed record ImpliedTagsRebuilt(int LinksChanged);

/// <summary>What recounting tag totals reports, which is only that it happened.</summary>
public sealed record TagsRecounted;

/// <summary>What deleting unused tags reports.</summary>
public sealed record UnusedTagsDeleted(int Count);

/// <summary>One of the Maintenance page's buttons: the work it queues and what the page calls it.</summary>
/// <param name="Key">Names the task in its form, its status URL and its section's anchor.</param>
public sealed record MaintenanceTask(string Key, string Title, JobWork Work)
{
    public static readonly MaintenanceTask Thumbnails = new("thumbnails", "Regenerate missing thumbnails",
        async job => await Get<PostService>(job).RegenerateMissingThumbnailsAsync(job, job.CancellationToken));

    public static readonly MaintenanceTask Hashes = new("hashes", "Compute missing perceptual hashes",
        async job => await Get<DuplicateService>(job).ComputeMissingHashesAsync(job, job.CancellationToken));

    public static readonly MaintenanceTask Duplicates = new("duplicates", "Scan for possible duplicates",
        async job => await Get<DuplicateService>(job).ScanForDuplicatesAsync(job, job.CancellationToken));

    public static readonly MaintenanceTask ImpliedTags = new("implied-tags", "Rebuild implied tags", async job =>
    {
        // One pass over the whole link table, with nothing in between to count.
        job.Report(new JobProgress("Rebuilding implied tags"));
        return new ImpliedTagsRebuilt(await Get<TagService>(job).RebuildAllImpliedAsync(job.CancellationToken));
    });

    public static readonly MaintenanceTask TagCounts = new("tag-counts", "Recount tag totals", async job =>
    {
        await Get<TagService>(job).RecountTagsAsync(job.CancellationToken);
        return new TagsRecounted();
    });

    public static readonly MaintenanceTask UnusedTags = new("unused-tags", "Delete unused tags",
        async job => new UnusedTagsDeleted(await Get<TagService>(job).DeleteUnusedTagsAsync(job.CancellationToken)));

    /// <summary>Every task, in the order the page shows them.</summary>
    public static readonly IReadOnlyList<MaintenanceTask> All =
        [Thumbnails, Hashes, Duplicates, ImpliedTags, TagCounts, UnusedTags];

    /// <summary>What the queue files these runs under, distinct from any other kind of job.</summary>
    public string Kind => $"maintenance/{Key}";

    public static MaintenanceTask? Find(string? key) => All.FirstOrDefault(t => t.Key == key);

    public static MaintenanceTask? ForKind(string kind) => All.FirstOrDefault(t => t.Kind == kind);

    private static T Get<T>(JobContext job) where T : notnull => job.Services.GetRequiredService<T>();
}
