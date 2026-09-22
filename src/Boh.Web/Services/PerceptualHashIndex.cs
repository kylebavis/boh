using Boh.Web.Data;
using Boh.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Boh.Web.Services;

/// <summary>Every perceptual hash, as parallel arrays sorted by post id.</summary>
public sealed record PerceptualHashSnapshot(int[] Ids, long[] Hashes)
{
    /// <summary>The post's hash, or null when it has none or does not exist.</summary>
    public long? HashOf(int postId)
    {
        var i = Array.BinarySearch(Ids, postId);
        return i >= 0 ? Hashes[i] : null;
    }
}

/// <summary>
/// Keeps every perceptual hash in memory, so a post page does not read the whole column
/// to find its look-alikes.
/// </summary>
/// <remarks>
/// Cleared by EF whenever a post is added or deleted or its hash changes, and reloaded on
/// the next read. Bulk <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> on posts bypasses that and
/// must call <see cref="Invalidate"/>.
/// </remarks>
public sealed class PerceptualHashIndex
{
    private readonly Lock gate = new();
    private PerceptualHashSnapshot? current;
    private long generation;

    public IInterceptor[] Interceptors { get; }

    public PerceptualHashIndex()
    {
        Interceptors = new CommittedChanges<int>(ChangedPosts, _ => Invalidate()).Interceptors;
    }

    public async Task<PerceptualHashSnapshot> GetAsync(BohDbContext db, CancellationToken ct)
    {
        var snapshot = Volatile.Read(ref current);
        if (snapshot is not null) return snapshot;

        long started;
        lock (gate) started = generation;

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

        snapshot = new PerceptualHashSnapshot(ids, hashes);

        // A change committed while this was reading may be missing from it, so it is only
        // kept if nothing was invalidated in the meantime.
        lock (gate)
        {
            if (generation == started) current = snapshot;
        }

        return snapshot;
    }

    public void Invalidate()
    {
        lock (gate)
        {
            generation++;
            current = null;
        }
    }

    private static IEnumerable<int> ChangedPosts(DbContext context) =>
        context.ChangeTracker.Entries<Post>()
            .Where(e => e.State is EntityState.Added or EntityState.Deleted
                || (e.State == EntityState.Modified && e.Property(p => p.PerceptualHash).IsModified))
            .Select(e => e.Entity.Id);
}
