using Boh.Web.Data;
using Boh.Web.Data.Entities;
using Boh.Web.Jobs;
using Boh.Web.Tags;
using Boh.Web.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace Boh.Web.Services;

public abstract record PostCreateResult
{
    private PostCreateResult() { }

    /// <summary>The file was stored. <paramref name="Similar"/> lists look-alikes, informational only.</summary>
    public sealed record Created(Post Post, IReadOnlyList<SimilarPost> Similar) : PostCreateResult;

    /// <summary>The same bytes are already stored. <paramref name="SourceAdded"/>: the URL was new to that post.</summary>
    public sealed record Duplicate(int ExistingPostId, bool SourceAdded = false) : PostCreateResult;

    public sealed record Rejected(string Reason) : PostCreateResult;
}

/// <summary>Outcome of a thumbnail repair pass over every post.</summary>
public sealed record ThumbnailRepairResult(int Missing, int Regenerated, int Failed);

public sealed class PostService(
    BohDbContext db,
    ContentAddressedFileStore store,
    MediaProcessorRegistry processors,
    DuplicateService duplicates,
    BohOptions options,
    ILogger<PostService> logger)
{
    /// <summary>Decompression-bomb guard.</summary>
    private const long MaxPixels = 400_000_000;

    private const int SimilarOnCreate = 4;

    /// <summary>Stores a file as a post, or reports the post already holding those bytes. The source is recorded either way.</summary>
    public async Task<PostCreateResult> CreateAsync(
        Stream content,
        int? uploadedById,
        string sourceUrl,
        CancellationToken ct)
    {
        var staged = await store.StageAsync(content, ct);
        var source = NormalizeSource(sourceUrl);

        try
        {
            if (staged.Length == 0) return new PostCreateResult.Rejected("The file is empty.");

            var existingId = await db.Posts.AsNoTracking()
                .Where(p => p.Sha256 == staged.Sha256)
                .Select(p => (int?)p.Id)
                .FirstOrDefaultAsync(ct);

            if (existingId is not null)
            {
                return new PostCreateResult.Duplicate(
                    existingId.Value, await AddSourceAsync(existingId.Value, source, ct));
            }

            var probe = await processors.ProbeAsync(staged.TempPath, ct);
            if (probe is null)
                return new PostCreateResult.Rejected("Unrecognized or unsupported file type.");

            var (processor, info) = probe.Value;

            if ((long)info.Width * info.Height > MaxPixels)
                return new PostCreateResult.Rejected(
                    $"Dimensions {info.Width}x{info.Height} exceed the supported limit.");

            store.CommitOriginal(staged, info.Extension);

            await GenerateThumbnailAsync(processor, staged.Sha256, info.Extension, ct);

            // Video is not hashed, and not marked as tried.
            var perceptualHash = info.IsVideo
                ? null
                : await ComputePerceptualHashAsync(processor, staged.Sha256, info.Extension, ct);

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
                PerceptualHash = perceptualHash,
                PerceptualHashTried = !info.IsVideo,
                UploadedAt = DateTimeOffset.UtcNow,
                UploadedById = uploadedById
            };

            if (source is not null) post.Sources.Add(new PostSource { Url = source });

            db.Posts.Add(post);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Probably a concurrent insert of the same bytes. Detach the post and its source before retrying as a duplicate.
                foreach (var pending in post.Sources) db.Entry(pending).State = EntityState.Detached;
                db.Entry(post).State = EntityState.Detached;

                var racedId = await FindBySha(staged.Sha256, ct);
                if (racedId is null) throw;

                return new PostCreateResult.Duplicate(
                    racedId.Value, await AddSourceAsync(racedId.Value, source, ct));
            }

            // After the insert, so the post can exclude itself.
            var similar = perceptualHash is null
                ? []
                : await duplicates.FindSimilarAsync(perceptualHash.Value, post.Id, SimilarOnCreate, ct);

            return new PostCreateResult.Created(post, similar);
        }
        finally
        {
            // No-op once CommitOriginal has moved the file into place.
            store.Discard(staged);
        }
    }

    /// <summary>Records another origin. True only when a row was added; blank or known URLs are no-ops.</summary>
    public async Task<bool> AddSourceAsync(int postId, string? url, CancellationToken ct)
    {
        var source = NormalizeSource(url);
        if (source is null) return false;

        if (await db.PostSources.AnyAsync(s => s.PostId == postId && s.Url == source, ct)) return false;

        var row = new PostSource { PostId = postId, Url = source };
        db.PostSources.Add(row);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Concurrent insert of the same URL. Detach, as imports reuse the context.
            db.Entry(row).State = EntityState.Detached;

            // No URL in the log: it's user input.
            logger.LogDebug(ex, "A source was already recorded on post {PostId}", postId);
            return false;
        }

        return true;
    }

    /// <summary>Scoped to the post so a forged id can't reach another post's source.</summary>
    public async Task<bool> RemoveSourceAsync(int postId, int sourceId, CancellationToken ct) =>
        await db.PostSources
            .Where(s => s.Id == sourceId && s.PostId == postId)
            .ExecuteDeleteAsync(ct) > 0;

    /// <summary>Canonical URL, or null when there is nothing worth recording.</summary>
    private static string? NormalizeSource(string? url) =>
        SourceUrls.TryCanonicalize(url, out var canonical) ? canonical : null;

    /// <summary>Regenerates missing thumbnails, re-probing each original.</summary>
    public async Task<ThumbnailRepairResult> RegenerateMissingThumbnailsAsync(
        IProgress<JobProgress>? progress, CancellationToken ct)
    {
        var posts = await db.Posts.AsNoTracking()
            .OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.Sha256, p.FileExtension })
            .ToListAsync(ct);

        int missing = 0, regenerated = 0, failed = 0;

        for (var i = 0; i < posts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            progress?.Report(new JobProgress("Checking posts", i, posts.Count));

            var post = posts[i];
            if (store.ThumbExists(post.Sha256)) continue;
            missing++;

            if (!store.OriginalExists(post.Sha256, post.FileExtension))
            {
                logger.LogWarning("Post {PostId} has no original at {Sha256}; cannot rebuild its thumbnail",
                    post.Id, post.Sha256);
                failed++;
                continue;
            }

            try
            {
                var probed = await processors.ProbeAsync(store.OriginalPath(post.Sha256, post.FileExtension), ct);
                if (probed is null)
                {
                    logger.LogWarning("No processor recognizes the original for post {PostId}", post.Id);
                    failed++;
                }
                else if (await GenerateThumbnailAsync(probed.Value.Processor, post.Sha256, post.FileExtension, ct))
                {
                    regenerated++;
                }
                else
                {
                    failed++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to probe the original for post {PostId}", post.Id);
                failed++;
            }
        }

        progress?.Report(new JobProgress("Checking posts", posts.Count, posts.Count));

        logger.LogInformation(
            "Thumbnail repair: {Missing} missing, {Regenerated} rebuilt, {Failed} failed",
            missing, regenerated, failed);

        return new ThumbnailRepairResult(missing, regenerated, failed);
    }

    /// <summary>Logs rather than throws: <see cref="RegenerateMissingThumbnailsAsync"/> can repair it.</summary>
    private async Task<bool> GenerateThumbnailAsync(
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
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Thumbnail generation failed for {Sha256}", sha256);
            return false;
        }
    }

    /// <summary>Logs rather than throws: the hashing job can fill it in later.</summary>
    private async Task<long?> ComputePerceptualHashAsync(
        IMediaProcessor processor, string sha256, string extension, CancellationToken ct)
    {
        try
        {
            return await processor.TryComputePerceptualHashAsync(
                store.OriginalPath(sha256, extension), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Perceptual hashing failed for {Sha256}", sha256);
            return null;
        }
    }

    private Task<int?> FindBySha(string sha256, CancellationToken ct) =>
        db.Posts.AsNoTracking()
            .Where(p => p.Sha256 == sha256)
            .Select(p => (int?)p.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>Narrows a query by a resolved search. Null means nothing can match, so don't query.</summary>
    private async Task<IQueryable<Post>?> ApplySearchAsync(
        IQueryable<Post> query, ResolvedSearch? search, CancellationToken ct)
    {
        if (search is { Unsatisfiable: true }) return null;
        if (search is null) return query;

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

        foreach (var term in search.Predicates)
        {
            switch (term)
            {
                // Case-insensitive; SQLite lower() is ASCII-only, fine for URLs.
                case QueryTerm.SourceMatch(var text, var exclude):
                    var needle = text;
                    query = exclude
                        ? query.Where(p => !p.Sources.Any(s => s.Url.ToLower().Contains(needle)))
                        : query.Where(p => p.Sources.Any(s => s.Url.ToLower().Contains(needle)));
                    break;

                case QueryTerm.SourceMissing(var exclude):
                    query = exclude
                        ? query.Where(p => p.Sources.Any())
                        : query.Where(p => !p.Sources.Any());
                    break;

                case QueryTerm.SimilarTo(var postId, var exclude):
                    var alike = await duplicates.FindSimilarIdsAsync(postId, ct);

                    // No hash to compare: requiring matches nothing, excluding excludes nothing.
                    if (alike.Count == 0)
                    {
                        if (!exclude) return null;
                        break;
                    }

                    query = exclude
                        ? query.Where(p => !alike.Contains(p.Id))
                        : query.Where(p => alike.Contains(p.Id));
                    break;
            }
        }

        return query;
    }

    /// <summary>A random post within the search, via count + offset rather than ORDER BY RANDOM().</summary>
    public async Task<int?> GetRandomIdAsync(ResolvedSearch? search, CancellationToken ct)
    {
        var query = await ApplySearchAsync(db.Posts.AsNoTracking(), search, ct);
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
            .Include(p => p.Sources.OrderBy(s => s.Id))
            .Include(p => p.UploadedBy)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<(IReadOnlyList<GalleryPost> Posts, int TotalCount)> ListAsync(
        ResolvedSearch? search, int page, int pageSize, CancellationToken ct)
    {
        var query = await ApplySearchAsync(db.Posts.AsNoTracking(), search, ct);

        // A required tag that doesn't exist: nothing to query.
        if (query is null) return ([], 0);

        var ordered = query.OrderByDescending(p => p.UploadedAt).ThenByDescending(p => p.Id);

        var total = await ordered.CountAsync(ct);
        var posts = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new GalleryPost(p.Id, p.Sha256, p.Width, p.Height, p.IsVideo))
            .ToListAsync(ct);

        return (posts, total);
    }

    /// <summary>Removes a post and its blobs. Sha256 is unique, so no refcounting.</summary>
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

        // Only after the row is gone, so a failed delete never orphans a live post.
        store.DeleteBlobs(sha, extension);
        return true;
    }
}
