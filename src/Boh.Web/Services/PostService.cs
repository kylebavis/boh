using Boh.Web.Data;
using Boh.Web.Data.Entities;
using Boh.Web.Tags;
using Microsoft.EntityFrameworkCore;

namespace Boh.Web.Services;

public abstract record PostCreateResult
{
    private PostCreateResult() { }

    /// <summary>
    /// The file was stored. <paramref name="Similar"/> holds posts that look like it — empty
    /// in the ordinary case. A perceptual match is a suspicion rather than a fact, so it is
    /// reported alongside a post that was created regardless; see
    /// <see cref="DuplicateService.MaxDistance"/> for why nothing refuses an upload over one.
    /// </summary>
    public sealed record Created(Post Post, IReadOnlyList<SimilarPost> Similar) : PostCreateResult;

    /// <summary>
    /// The identical file is already stored; <paramref name="ExistingPostId"/> holds it.
    /// <paramref name="SourceAdded"/> is true when the incoming URL was not among that
    /// post's sources and has now been recorded on it.
    /// </summary>
    public sealed record Duplicate(int ExistingPostId, bool SourceAdded = false) : PostCreateResult;

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
    DuplicateService duplicates,
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

    /// <summary>
    /// How many look-alikes a newly stored post reports. Enough to show the upload was
    /// probably a repost; the post's own page lists the rest.
    /// </summary>
    private const int SimilarOnCreate = 4;

    /// <summary>
    /// Stores a file as a new post, or reports the post that already holds those bytes.
    /// </summary>
    /// <remarks>
    /// <paramref name="sourceUrl"/> is attached to whichever post ends up holding the
    /// content, new or already stored. Finding the same bytes at a second address is a fact
    /// about the file worth keeping, and dropping it was the only way the old single-URL
    /// column could handle it.
    /// </remarks>
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

            // Video is not hashed. Recorded as never attempted rather than attempted-and-empty,
            // so a release that learns how would find these posts waiting for the backfill —
            // see DuplicateService.ComputeMissingHashesAsync.
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
                // Most likely the unique index on Sha256: another request stored the same
                // content between our existence check and this insert. The pending source row
                // is detached alongside the post, or the retry below would try to insert it
                // again against a post id that was never assigned.
                foreach (var pending in post.Sources) db.Entry(pending).State = EntityState.Detached;
                db.Entry(post).State = EntityState.Detached;

                var racedId = await FindBySha(staged.Sha256, ct);
                if (racedId is null) throw;

                return new PostCreateResult.Duplicate(
                    racedId.Value, await AddSourceAsync(racedId.Value, source, ct));
            }

            // After the insert rather than before it, so the post can be excluded from its own
            // results by id instead of the search having to know it is about to exist.
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

    /// <summary>
    /// Records another origin for a post. Returns true only when a row was actually added,
    /// so a caller can tell "we learned something new about this file" from "we already knew".
    /// </summary>
    /// <remarks>
    /// Blank input and an address the post already carries are both ordinary outcomes rather
    /// than errors — re-importing the same gallery is a normal thing to do, and it must stay
    /// a no-op however many times it happens.
    /// </remarks>
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
            // The unique index on (PostId, Url): a concurrent import recorded the same address
            // first, which leaves the post in exactly the state this call wanted. Detaching
            // matters because an import reuses one context across every file it downloaded.
            db.Entry(row).State = EntityState.Detached;

            // The URL is deliberately not in the message. It originates with whoever submitted
            // it, and a log line is the wrong place to repeat user input — the post id and the
            // exception identify this race well enough to debug it.
            logger.LogDebug(ex, "A source was already recorded on post {PostId}", postId);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Removes one recorded source. Scoped to the post rather than keyed on the row id alone,
    /// so a stale or forged id cannot reach a source belonging to a different post.
    /// </summary>
    public async Task<bool> RemoveSourceAsync(int postId, int sourceId, CancellationToken ct) =>
        await db.PostSources
            .Where(s => s.Id == sourceId && s.PostId == postId)
            .ExecuteDeleteAsync(ct) > 0;

    /// <summary>
    /// Null means "nothing to record": an empty URL, which is what a direct upload passes, or
    /// one that is not an address anyone could follow. Validating and canonicalizing here
    /// rather than trusting callers means no entry point can put a junk or malformed value in
    /// the table — see <see cref="SourceUrls.TryCanonicalize"/> for why the rewrite matters.
    /// </summary>
    private static string? NormalizeSource(string? url) =>
        SourceUrls.TryCanonicalize(url, out var canonical) ? canonical : null;

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

    /// <summary>
    /// Hashes the committed original. Treated like thumbnail generation: a post without a hash
    /// is only a post that near-duplicate detection cannot see, which is no reason to fail an
    /// upload, and <see cref="DuplicateService.ComputeMissingHashesAsync"/> can fill it in later.
    /// </summary>
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

    /// <summary>
    /// Narrows a post query by a resolved search. Null means the search cannot match anything,
    /// which is distinct from matching nothing — the caller should not run a query at all.
    /// </summary>
    /// <remarks>
    /// Asynchronous because of <c>similar:</c>, which cannot be expressed in SQL: SQLite has
    /// no bit-count function, so "within eight bits of that post's hash" has to be answered in
    /// memory first and the resulting ids folded into the query.
    /// </remarks>
    private async Task<IQueryable<Post>?> ApplySearchAsync(
        IQueryable<Post> query, ResolvedSearch? search, CancellationToken ct)
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

        foreach (var term in search.Predicates)
        {
            switch (term)
            {
                // Lowercasing both sides rather than relying on the column's collation: a
                // stored path keeps whatever case it arrived with, and a search typed in
                // another case should still find it. SQLite's lower() is ASCII-only, which a
                // URL never exceeds in the part anyone searches by.
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

                    // No hash on the reference post — or no such post — means the question has
                    // no answer. Requiring an unanswerable term matches nothing, the same as
                    // requiring a tag that does not exist; excluding it excludes nothing.
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

    public async Task<(IReadOnlyList<Post> Posts, int TotalCount)> ListAsync(
        ResolvedSearch? search, int page, int pageSize, CancellationToken ct)
    {
        var query = await ApplySearchAsync(db.Posts.AsNoTracking(), search, ct);

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
