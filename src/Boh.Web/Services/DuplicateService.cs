using Boh.Web.Data;
using Boh.Web.Data.Entities;
using Boh.Web.Jobs;
using Boh.Web.Media;
using Microsoft.EntityFrameworkCore;

namespace Boh.Web.Services;

/// <summary>A post whose perceptual hash is close to another's, and how close.</summary>
public sealed record SimilarPost(int PostId, int Distance);

/// <summary>The same finding with the post loaded, for rendering a thumbnail.</summary>
public sealed record SimilarPostCard(Post Post, int Distance);

/// <summary>Outcome of a hashing pass over every post that has no hash.</summary>
/// <param name="Pending">Posts that had no hash when the pass started.</param>
/// <param name="Hashed">Posts that now have one.</param>
/// <param name="Featureless">Posts with nothing to hash, recorded so they are not retried.</param>
/// <param name="Failed">
/// Posts whose original could not be read at all. They are deliberately left pending, so a
/// later pass — once a missing mount is back, say — picks them up.
/// </param>
public sealed record HashingResult(int Pending, int Hashed, int Featureless, int Failed);

/// <summary>
/// A group of posts that all look alike, oldest first — which is usually the one to keep.
/// <paramref name="ClosestDistance"/> is the tightest match inside the group.
/// </summary>
/// <param name="Posts">The members to show, which is all of them until a group gets large.</param>
/// <param name="Size">
/// How many posts the group really holds. Larger than <paramref name="Posts"/> for a group
/// nobody could work through in one sitting, where loading and rendering every member would
/// cost far more than it tells the reader.
/// </param>
public sealed record DuplicateCluster(IReadOnlyList<Post> Posts, int Size, int ClosestDistance);

/// <summary>Outcome of an archive-wide scan.</summary>
/// <param name="OmittedClusters">Groups found beyond the ones the report shows.</param>
public sealed record DuplicateScan(
    int Hashed,
    IReadOnlyList<DuplicateCluster> Clusters,
    int OmittedClusters);

/// <summary>
/// Near-duplicate detection over perceptual hashes: everything that asks "what else looks
/// like this", as opposed to the byte-identical check <see cref="PostService"/> does against
/// <see cref="Post.Sha256"/>.
/// </summary>
/// <remarks>
/// Every query here reads all the hashes and compares them in memory. A perceptual hash
/// cannot be matched with an index — the question is never "which row equals this" but
/// "which rows are within eight bits of it" — so the alternatives are a linear scan or a
/// purpose-built structure (a BK-tree, or multi-index hashing) maintained alongside the
/// table. At the scale this project targets the scan is not worth avoiding: the hashes are
/// eight bytes each and served by a covering index, and a hundred thousand of them cost one
/// small read and about a hundred thousand popcounts, which is well under a millisecond of
/// CPU. The archive-wide scan is the one place that stops being true, because it compares
/// every pair rather than one hash against every other, which is why it runs as a background
/// job.
/// </remarks>
public sealed class DuplicateService(
    BohDbContext db,
    IFileStore store,
    MediaProcessorRegistry processors,
    ILogger<DuplicateService> logger)
{
    /// <summary>
    /// How many of the 63 hash bits may differ before two images are no longer considered the
    /// same picture. Eight is deliberately cautious: re-encoding, resizing and light
    /// watermarking move a handful of bits, while unrelated images sit near the middle of the
    /// range — around 31 bits apart — so the gap either side of this line is wide. Raising it
    /// finds crops and heavier edits at the cost of pairs that merely share a composition,
    /// which is why nothing here refuses an upload on the strength of it.
    /// </summary>
    public const int MaxDistance = 8;

    /// <summary>
    /// Posts a hashing pass loads and commits together. Small enough that the context never
    /// tracks much at once and a cancelled pass loses little work; large enough that committing
    /// is not the cost — decoding is.
    /// </summary>
    private const int HashingBatchSize = 200;

    /// <summary>
    /// Clusters a single report will render. A collection with a large set of near-identical
    /// posts would otherwise produce a page nobody can act on.
    /// </summary>
    private const int ScanMaxClusters = 50;

    /// <summary>
    /// Members of one group the report will load and render. A group is a set of things that
    /// look alike, so the twelfth thumbnail tells the reader nothing the third did not — and
    /// without a cap, one enormous group would put thousands of ids into a query.
    /// </summary>
    private const int ScanMaxClusterPosts = 12;

    /// <summary>
    /// Posts a <c>similar:</c> search may resolve to. The threshold keeps this small in
    /// practice; the cap is here so a pathological hash cannot build an unbounded query.
    /// </summary>
    private const int SearchMaxMatches = 500;

    private enum HashOutcome
    {
        Hashed,
        Featureless,
        Failed
    }

    /// <summary>
    /// Posts that look like <paramref name="hash"/>, closest first.
    /// <paramref name="excludePostId"/> keeps a post out of its own results.
    /// </summary>
    public async Task<IReadOnlyList<SimilarPost>> FindSimilarAsync(
        long hash, int? excludePostId, int limit, CancellationToken ct)
    {
        var (ids, hashes) = await LoadHashesAsync(ct);
        var found = new List<SimilarPost>();

        for (var i = 0; i < ids.Length; i++)
        {
            if (ids[i] == excludePostId) continue;

            var distance = PerceptualHash.Distance(hash, hashes[i]);
            if (distance <= MaxDistance) found.Add(new SimilarPost(ids[i], distance));
        }

        return [.. found.OrderBy(f => f.Distance).ThenByDescending(f => f.PostId).Take(limit)];
    }

    /// <summary>
    /// What else looks like a given post, loaded for display. Empty when the post has no hash,
    /// which is also what a caller gets for a post that does not exist.
    /// </summary>
    public async Task<IReadOnlyList<SimilarPostCard>> GetSimilarToPostAsync(
        int postId, int limit, CancellationToken ct)
    {
        var hash = await HashOfAsync(postId, ct);
        if (hash is null) return [];

        var found = await FindSimilarAsync(hash.Value, postId, limit, ct);
        if (found.Count == 0) return [];

        var ids = found.Select(f => f.PostId).ToList();
        var posts = await db.Posts.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        // Preserves the closest-first ordering the scan produced, which the lookup lost.
        return
        [
            .. found
                .Where(f => posts.ContainsKey(f.PostId))
                .Select(f => new SimilarPostCard(posts[f.PostId], f.Distance))
        ];
    }

    /// <summary>
    /// The post ids a <c>similar:</c> search matches, the reference post included — the point
    /// of that search is to put a post beside its look-alikes and compare them.
    /// </summary>
    /// <remarks>
    /// Empty when the post has no hash or does not exist. That is the honest answer to
    /// "what looks like this", and it leaves the caller to decide what an unanswerable
    /// requirement means for the search as a whole.
    /// </remarks>
    public async Task<IReadOnlyList<int>> FindSimilarIdsAsync(int postId, CancellationToken ct)
    {
        var hash = await HashOfAsync(postId, ct);
        if (hash is null) return [];

        var found = await FindSimilarAsync(hash.Value, postId, SearchMaxMatches - 1, ct);
        return [postId, .. found.Select(f => f.PostId)];
    }

    /// <summary>
    /// Hashes posts that have none — every post in an archive that predates this feature, and
    /// anything whose hashing failed at upload.
    /// </summary>
    /// <remarks>
    /// Video is left alone rather than marked as attempted: nothing has tried it, and a
    /// release that learns to hash video should find those posts still waiting here.
    /// </remarks>
    public async Task<HashingResult> ComputeMissingHashesAsync(
        IProgress<JobProgress>? progress, CancellationToken ct)
    {
        var pending = db.Posts.Where(p => !p.PerceptualHashTried && !p.IsVideo);

        var total = await pending.CountAsync(ct);
        int done = 0, hashed = 0, featureless = 0, failed = 0;
        var after = 0;

        while (true)
        {
            // Paged by id rather than by what is still pending: a failure stays pending, so
            // asking again for "the next pending posts" would return it every time and never
            // reach the posts after it.
            var batch = await pending
                .Where(p => p.Id > after)
                .OrderBy(p => p.Id)
                .Take(HashingBatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0) break;

            foreach (var post in batch)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new JobProgress("Hashing posts", done, total));

                switch (await HashAsync(post, ct))
                {
                    case HashOutcome.Hashed: hashed++; break;
                    case HashOutcome.Featureless: featureless++; break;
                    default: failed++; break;
                }

                done++;
            }

            // One transaction per batch. Saving per post would fsync for every post in a pass
            // whose real cost is decoding.
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();

            after = batch[^1].Id;
        }

        progress?.Report(new JobProgress("Hashing posts", done, total));

        logger.LogInformation(
            "Perceptual hashing: {Pending} pending, {Hashed} hashed, {Featureless} featureless, {Failed} failed",
            total, hashed, featureless, failed);

        return new HashingResult(total, hashed, featureless, failed);
    }

    /// <summary>Hashes one post in place, leaving the caller to save it.</summary>
    private async Task<HashOutcome> HashAsync(Post post, CancellationToken ct)
    {
        if (!store.OriginalExists(post.Sha256, post.FileExtension))
        {
            // Left un-tried on purpose: an original missing because a mount is offline
            // comes back, and a later pass should hash it then rather than write it off now.
            logger.LogWarning("Post {PostId} has no original at {Sha256}; cannot hash it",
                post.Id, post.Sha256);
            return HashOutcome.Failed;
        }

        var originalPath = store.OriginalPath(post.Sha256, post.FileExtension);

        try
        {
            // Re-probed rather than trusting the stored MIME type, so the same processor
            // handles it as at upload — and a post whose original has since become
            // unreadable is reported instead of throwing.
            var probed = await processors.ProbeAsync(originalPath, ct);
            if (probed is null)
            {
                logger.LogWarning("No processor recognizes the original for post {PostId}", post.Id);
                return HashOutcome.Failed;
            }

            var hash = await probed.Value.Processor.TryComputePerceptualHashAsync(originalPath, ct);

            post.PerceptualHash = hash;
            post.PerceptualHashTried = true;

            return hash is null ? HashOutcome.Featureless : HashOutcome.Hashed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to compute a perceptual hash for post {PostId}", post.Id);
            return HashOutcome.Failed;
        }
    }

    /// <summary>
    /// Groups the whole archive into sets of posts that look alike, for finding duplicates
    /// that were stored before anything was watching for them.
    /// </summary>
    /// <remarks>
    /// Transitive by construction: A near B and B near C puts all three in one group even when
    /// A and C are further apart than the threshold. That is the useful shape for a report —
    /// a chain of re-encodings is one duplicate to resolve, not several overlapping pairs.
    /// </remarks>
    public async Task<DuplicateScan> ScanForDuplicatesAsync(
        IProgress<JobProgress>? progress, CancellationToken ct)
    {
        var (ids, hashes) = await LoadHashesAsync(ct);
        var count = ids.Length;

        // Union-find over the hash array. Groups form as pairs are discovered, so the pass
        // needs no list of pairs — which matters, because a collection with a thousand
        // near-identical posts has half a million of them.
        var parent = new int[count];
        var closest = new int[count];

        for (var i = 0; i < count; i++)
        {
            parent[i] = i;
            closest[i] = int.MaxValue;
        }

        for (var i = 0; i < count; i++)
        {
            // Once per row rather than per pair: a row is one sweep over the hashes, and the
            // inner loop is the part worth keeping tight.
            ct.ThrowIfCancellationRequested();
            progress?.Report(new JobProgress("Comparing posts", i, count));

            var hash = hashes[i];

            for (var j = i + 1; j < count; j++)
            {
                var distance = PerceptualHash.Distance(hash, hashes[j]);
                if (distance <= MaxDistance) Merge(i, j, distance);
            }
        }

        progress?.Report(new JobProgress("Comparing posts", count, count));

        var groups = new Dictionary<int, List<int>>();
        for (var i = 0; i < count; i++)
        {
            var root = Root(i);
            if (!groups.TryGetValue(root, out var members)) groups[root] = members = [];
            members.Add(i);
        }

        var found = groups
            .Where(g => g.Value.Count > 1)
            // Tightest match first: an exact-looking pair is likelier to be a real duplicate
            // than a group that only just cleared the threshold.
            .OrderBy(g => closest[g.Key])
            .ThenByDescending(g => g.Value.Count)
            .ToList();

        // Post ids per group, oldest first — ids ascend with age because LoadHashesAsync reads
        // them in order — and trimmed to what the report will actually show.
        var shown = found
            .Take(ScanMaxClusters)
            .Select(g => (
                Members: g.Value.Select(index => ids[index]).Take(ScanMaxClusterPosts).ToList(),
                Size: g.Value.Count,
                Closest: closest[g.Key]))
            .ToList();

        var wanted = shown.SelectMany(g => g.Members).ToList();

        var posts = await db.Posts.AsNoTracking()
            .Where(p => wanted.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        var clusters = shown
            .Select(g => new DuplicateCluster(
                [.. g.Members.Select(posts.GetValueOrDefault).OfType<Post>()],
                g.Size,
                g.Closest))
            // A post deleted between the two queries can leave a group of one, which is no
            // longer a duplicate of anything.
            .Where(c => c.Posts.Count > 1)
            .ToList();

        logger.LogInformation(
            "Duplicate scan: {Hashed} hashed posts, {Clusters} cluster(s)", count, found.Count);

        return new DuplicateScan(count, clusters, found.Count - clusters.Count);

        int Root(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];   // halve the path on the way up
                x = parent[x];
            }

            return x;
        }

        void Merge(int a, int b, int distance)
        {
            var rootA = Root(a);
            var rootB = Root(b);

            if (rootA == rootB)
            {
                closest[rootA] = Math.Min(closest[rootA], distance);
                return;
            }

            parent[rootB] = rootA;
            closest[rootA] = Math.Min(Math.Min(closest[rootA], closest[rootB]), distance);
        }
    }

    private Task<long?> HashOfAsync(int postId, CancellationToken ct) =>
        db.Posts.AsNoTracking()
            .Where(p => p.Id == postId)
            .Select(p => p.PerceptualHash)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Every hash in the collection, as parallel arrays so the comparison loops stay a tight
    /// walk over two contiguous blocks of memory.
    /// </summary>
    private async Task<(int[] Ids, long[] Hashes)> LoadHashesAsync(CancellationToken ct)
    {
        var rows = await db.Posts.AsNoTracking()
            .Where(p => p.PerceptualHash != null)
            .OrderBy(p => p.Id)
            .Select(p => new { p.Id, Hash = p.PerceptualHash!.Value })
            .ToListAsync(ct);

        var ids = new int[rows.Count];
        var hashes = new long[rows.Count];

        for (var i = 0; i < rows.Count; i++)
        {
            ids[i] = rows[i].Id;
            hashes[i] = rows[i].Hash;
        }

        return (ids, hashes);
    }
}
