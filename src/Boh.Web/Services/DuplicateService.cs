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
/// <param name="Failed">Unreadable originals, left pending for a later pass.</param>
public sealed record HashingResult(int Pending, int Hashed, int Featureless, int Failed);

/// <summary>
/// Posts that look alike, oldest first. <paramref name="Posts"/> may be capped;
/// <paramref name="Size"/> is the real count.
/// </summary>
public sealed record DuplicateCluster(IReadOnlyList<Post> Posts, int Size, int ClosestDistance);

/// <summary>Outcome of an archive-wide scan.</summary>
/// <param name="OmittedClusters">Groups found beyond the ones the report shows.</param>
public sealed record DuplicateScan(
    int Hashed,
    IReadOnlyList<DuplicateCluster> Clusters,
    int OmittedClusters);

/// <summary>
/// Near-duplicate detection over perceptual hashes, by linear scan of the in-memory
/// <see cref="PerceptualHashIndex"/>. Cheap at this scale except the all-pairs scan, which runs as a job.
/// </summary>
public sealed class DuplicateService(
    BohDbContext db,
    PerceptualHashIndex hashIndex,
    ContentAddressedFileStore store,
    MediaProcessorRegistry processors,
    ILogger<DuplicateService> logger)
{
    /// <summary>
    /// Differing bits (of 63) still counted as the same picture. Cautious: re-encodes move a
    /// few bits, unrelated images sit ~31 apart.
    /// </summary>
    public const int MaxDistance = 8;

    private const int HashingBatchSize = 200;

    private const int ScanMaxClusters = 50;

    private const int ScanMaxClusterPosts = 12;

    /// <summary>Bounds the query a <c>similar:</c> search builds.</summary>
    private const int SearchMaxMatches = 500;

    private enum HashOutcome
    {
        Hashed,
        Featureless,
        Failed
    }

    /// <summary>Posts that look like <paramref name="hash"/>, closest first.</summary>
    public async Task<IReadOnlyList<SimilarPost>> FindSimilarAsync(
        long hash, int? excludePostId, int limit, CancellationToken ct)
    {
        var (ids, hashes) = await hashIndex.GetAsync(db, ct);
        var found = new List<SimilarPost>();

        for (var i = 0; i < ids.Length; i++)
        {
            if (ids[i] == excludePostId) continue;

            var distance = PerceptualHash.Distance(hash, hashes[i]);
            if (distance <= MaxDistance) found.Add(new SimilarPost(ids[i], distance));
        }

        return [.. found.OrderBy(f => f.Distance).ThenByDescending(f => f.PostId).Take(limit)];
    }

    /// <summary>Look-alikes of a post, loaded for display. Empty when it has no hash.</summary>
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

        // Keep closest-first order.
        return
        [
            .. found
                .Where(f => posts.ContainsKey(f.PostId))
                .Select(f => new SimilarPostCard(posts[f.PostId], f.Distance))
        ];
    }

    /// <summary>
    /// Ids a <c>similar:</c> search matches, the reference post included. Empty when it has no hash.
    /// </summary>
    public async Task<IReadOnlyList<int>> FindSimilarIdsAsync(int postId, CancellationToken ct)
    {
        var hash = await HashOfAsync(postId, ct);
        if (hash is null) return [];

        var found = await FindSimilarAsync(hash.Value, postId, SearchMaxMatches - 1, ct);
        return [postId, .. found.Select(f => f.PostId)];
    }

    /// <summary>Hashes posts that have none. Video is skipped, not marked as tried.</summary>
    public async Task<HashingResult> ComputeMissingHashesAsync(
        IProgress<JobProgress>? progress, CancellationToken ct)
    {
        var pending = db.Posts.Where(p => !p.PerceptualHashTried && !p.IsVideo);

        var total = await pending.CountAsync(ct);
        int done = 0, hashed = 0, featureless = 0, failed = 0;
        var after = 0;

        while (true)
        {
            // Paged by id: failures stay pending and would otherwise repeat forever.
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
            // Left un-tried: an offline mount may come back.
            logger.LogWarning("Post {PostId} has no original at {Sha256}; cannot hash it",
                post.Id, post.Sha256);
            return HashOutcome.Failed;
        }

        var originalPath = store.OriginalPath(post.Sha256, post.FileExtension);

        try
        {
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

    /// <summary>Groups the archive into look-alike sets. Transitive: A~B and B~C is one group.</summary>
    public async Task<DuplicateScan> ScanForDuplicatesAsync(
        IProgress<JobProgress>? progress, CancellationToken ct)
    {
        var (ids, hashes) = await hashIndex.GetAsync(db, ct);
        var count = ids.Length;

        // Union-find, so no list of pairs is held.
        var parent = new int[count];
        var closest = new int[count];

        for (var i = 0; i < count; i++)
        {
            parent[i] = i;
            closest[i] = int.MaxValue;
        }

        for (var i = 0; i < count; i++)
        {
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
            // Tightest match first.
            .OrderBy(g => closest[g.Key])
            .ThenByDescending(g => g.Value.Count)
            .ToList();

        // Oldest first: the snapshot is ordered by id.
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
            // A concurrent delete can leave a group of one.
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

    private async Task<long?> HashOfAsync(int postId, CancellationToken ct) =>
        (await hashIndex.GetAsync(db, ct)).HashOf(postId);
}
