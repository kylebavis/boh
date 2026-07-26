using Boh.Web.Data;
using Boh.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Boh.Web.Services;

public abstract record PostCreateResult
{
    private PostCreateResult() { }

    public sealed record Created(Post Post) : PostCreateResult;

    /// <summary>The identical file is already stored; <paramref name="ExistingPostId"/> holds it.</summary>
    public sealed record Duplicate(int ExistingPostId) : PostCreateResult;

    public sealed record Rejected(string Reason) : PostCreateResult;
}

/// <summary>
/// Outcome of a thumbnail repair pass. <paramref name="Remaining"/> is non-zero when the
/// run hit its budget before finishing, in which case running it again continues.
/// </summary>
public sealed record ThumbnailRepairResult(
    int Missing,
    int Regenerated,
    int Failed,
    int Remaining)
{
    public bool Complete => Remaining == 0;
}

public sealed class PostService(
    BohDbContext db,
    IFileStore store,
    MediaProcessorRegistry processors,
    BohOptions options,
    ILogger<PostService> logger)
{
    /// <summary>
    /// Guards against decompression bombs: a small file can declare enormous dimensions,
    /// and decoding it would allocate pixels * 4 bytes before anything else could intervene.
    /// </summary>
    private const long MaxPixels = 400_000_000;

    /// <summary>
    /// Bounds on a single repair pass. Regeneration re-decodes every original, so an archive
    /// of any size would outlive an HTTP request; the pass stops at whichever limit it meets
    /// first and reports what is left so the operator can simply run it again.
    /// </summary>
    private static readonly TimeSpan RepairTimeBudget = TimeSpan.FromSeconds(60);
    private const int RepairMaxPerRun = 500;

    public async Task<PostCreateResult> CreateAsync(
        Stream content,
        int? uploadedById,
        string sourceUrl,
        CancellationToken ct)
    {
        var staged = await store.StageAsync(content, ct);

        try
        {
            if (staged.Length == 0) return new PostCreateResult.Rejected("The file is empty.");

            var existingId = await db.Posts.AsNoTracking()
                .Where(p => p.Sha256 == staged.Sha256)
                .Select(p => (int?)p.Id)
                .FirstOrDefaultAsync(ct);

            if (existingId is not null) return new PostCreateResult.Duplicate(existingId.Value);

            var probe = await processors.ProbeAsync(staged.TempPath, ct);
            if (probe is null)
                return new PostCreateResult.Rejected("Unrecognized or unsupported file type.");

            var (processor, info) = probe.Value;

            if ((long)info.Width * info.Height > MaxPixels)
                return new PostCreateResult.Rejected(
                    $"Dimensions {info.Width}x{info.Height} exceed the supported limit.");

            store.CommitOriginal(staged, info.Extension);

            await GenerateThumbnailAsync(processor, staged.Sha256, info.Extension, ct);

            var post = new Post
            {
                Sha256 = staged.Sha256,
                FileExtension = info.Extension,
                MimeType = info.MimeType,
                FileSizeBytes = staged.Length,
                Width = info.Width,
                Height = info.Height,
                DurationSec = info.DurationSec,
                IsVideo = info.IsVideo,
                SourceUrl = sourceUrl,
                UploadedAt = DateTimeOffset.UtcNow,
                UploadedById = uploadedById
            };

            db.Posts.Add(post);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Most likely the unique index on Sha256: another request stored the same
                // content between our existence check and this insert.
                db.Entry(post).State = EntityState.Detached;

                var racedId = await FindBySha(staged.Sha256, ct);
                if (racedId is null) throw;

                return new PostCreateResult.Duplicate(racedId.Value);
            }

            return new PostCreateResult.Created(post);
        }
        finally
        {
            // No-op once CommitOriginal has moved the file into place.
            store.Discard(staged);
        }
    }

    /// <summary>
    /// A missing thumbnail degrades the gallery but does not invalidate the post, so a
    /// failure here is logged rather than propagated.
    /// <see cref="RegenerateMissingThumbnailsAsync"/> recovers anything that failed here.
    /// </summary>
    /// <summary>
    /// Regenerates thumbnails for posts that have none — whether generation failed at upload,
    /// the thumbnail directory was cleared, or it was lost moving between storage.
    /// </summary>
    /// <remarks>
    /// Deliberately re-probes each original rather than trusting the stored MIME type, so the
    /// same processor selection runs as at upload and a post whose original has since become
    /// unreadable is reported instead of throwing.
    /// </remarks>
    public async Task<ThumbnailRepairResult> RegenerateMissingThumbnailsAsync(CancellationToken ct)
    {
        var posts = await db.Posts.AsNoTracking()
            .OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.Sha256, p.FileExtension })
            .ToListAsync(ct);

        var started = System.Diagnostics.Stopwatch.StartNew();
        int missing = 0, regenerated = 0, failed = 0, remaining = 0;

        foreach (var post in posts)
        {
            ct.ThrowIfCancellationRequested();

            if (store.ThumbExists(post.Sha256)) continue;
            missing++;

            // Out of budget: count the rest so the caller can report honest progress.
            if (started.Elapsed >= RepairTimeBudget || regenerated + failed >= RepairMaxPerRun)
            {
                remaining++;
                continue;
            }

            if (!store.OriginalExists(post.Sha256, post.FileExtension))
            {
                logger.LogWarning("Post {PostId} has no original at {Sha256}; cannot rebuild its thumbnail",
                    post.Id, post.Sha256);
                failed++;
                continue;
            }

            var originalPath = store.OriginalPath(post.Sha256, post.FileExtension);

            try
            {
                var probed = await processors.ProbeAsync(originalPath, ct);
                if (probed is null)
                {
                    logger.LogWarning("No processor recognizes the original for post {PostId}", post.Id);
                    failed++;
                    continue;
                }

                store.EnsureThumbDirectory(post.Sha256);
                await probed.Value.Processor.GenerateThumbnailAsync(
                    originalPath, store.ThumbPath(post.Sha256), options.ThumbnailMaxEdge, ct);

                regenerated++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to regenerate the thumbnail for post {PostId}", post.Id);
                failed++;
            }
        }

        logger.LogInformation(
            "Thumbnail repair: {Missing} missing, {Regenerated} rebuilt, {Failed} failed, {Remaining} left",
            missing, regenerated, failed, remaining);

        return new ThumbnailRepairResult(missing, regenerated, failed, remaining);
    }

    private async Task GenerateThumbnailAsync(
        IMediaProcessor processor, string sha256, string extension, CancellationToken ct)
    {
        try
        {
            store.EnsureThumbDirectory(sha256);
            await processor.GenerateThumbnailAsync(
                store.OriginalPath(sha256, extension),
                store.ThumbPath(sha256),
                options.ThumbnailMaxEdge,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Thumbnail generation failed for {Sha256}", sha256);
        }
    }

    private Task<int?> FindBySha(string sha256, CancellationToken ct) =>
        db.Posts.AsNoTracking()
            .Where(p => p.Sha256 == sha256)
            .Select(p => (int?)p.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Narrows a post query by a resolved search. Null means the search cannot match anything,
    /// which is distinct from matching nothing — the caller should not run a query at all.
    /// </summary>
    private static IQueryable<Post>? ApplySearch(IQueryable<Post> query, ResolvedSearch? search)
    {
        if (search is { Unsatisfiable: true }) return null;
        if (search is null) return query;

        // Filtering on tag id rather than name keeps this on the PostTags index and
        // avoids repeating string comparisons per term.
        foreach (var tagId in search.Include)
        {
            var id = tagId;
            query = query.Where(p => p.PostTags.Any(pt => pt.TagId == id));
        }

        foreach (var tagId in search.Exclude)
        {
            var id = tagId;
            query = query.Where(p => !p.PostTags.Any(pt => pt.TagId == id));
        }

        return query;
    }

    /// <summary>
    /// Picks a post at random, honouring the active search so "random" stays within whatever
    /// the user is currently looking at. Returns null only when nothing matches.
    /// </summary>
    /// <remarks>
    /// Ordering the whole table by RANDOM() would sort every row to take one. Counting first
    /// and skipping to an offset costs an indexed count plus a single-row read, which stays
    /// flat as the collection grows.
    /// </remarks>
    public async Task<int?> GetRandomIdAsync(ResolvedSearch? search, CancellationToken ct)
    {
        var query = ApplySearch(db.Posts.AsNoTracking(), search);
        if (query is null) return null;

        var total = await query.CountAsync(ct);
        if (total == 0) return null;

        var offset = Random.Shared.Next(total);

        return await query
            .OrderBy(p => p.Id)
            .Skip(offset)
            .Select(p => (int?)p.Id)
            .FirstOrDefaultAsync(ct);
    }

    public Task<Post?> GetAsync(int id, CancellationToken ct) =>
        db.Posts
            .Include(p => p.PostTags).ThenInclude(pt => pt.Tag)
            .Include(p => p.UploadedBy)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<(IReadOnlyList<Post> Posts, int TotalCount)> ListAsync(
        ResolvedSearch? search, int page, int pageSize, CancellationToken ct)
    {
        var query = ApplySearch(db.Posts.AsNoTracking(), search);

        // A required tag that does not exist cannot be satisfied by any post, so there is
        // nothing to query for.
        if (query is null) return ([], 0);

        var ordered = query.OrderByDescending(p => p.UploadedAt).ThenByDescending(p => p.Id);

        var total = await ordered.CountAsync(ct);
        var posts = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (posts, total);
    }

    /// <summary>
    /// Removes a post, its tag links, and its blobs. Because Sha256 is unique per post,
    /// no other post can reference the same blob, so deletion needs no reference counting.
    /// </summary>
    public async Task<bool> DeleteAsync(int postId, CancellationToken ct)
    {
        var post = await db.Posts
            .Include(p => p.PostTags)
            .FirstOrDefaultAsync(p => p.Id == postId, ct);

        if (post is null) return false;

        var tagIds = post.PostTags.Select(pt => pt.TagId).ToList();
        var sha = post.Sha256;
        var extension = post.FileExtension;

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            db.Posts.Remove(post);
            await db.SaveChangesAsync(ct);

            if (tagIds.Count > 0)
            {
                await db.Tags
                    .Where(t => tagIds.Contains(t.Id) && t.PostCount > 0)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.PostCount, t => t.PostCount - 1), ct);
            }

            await tx.CommitAsync(ct);
        }

        // Only after the row is durably gone, so a failed delete never orphans a live post.
        store.DeleteBlobs(sha, extension);
        return true;
    }
}
