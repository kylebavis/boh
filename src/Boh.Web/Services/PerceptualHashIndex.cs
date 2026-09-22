using System.Data.Common;
using System.Runtime.CompilerServices;
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

    // Contexts with a relevant change saved but not yet committed.
    private readonly ConditionalWeakTable<DbContext, object> pending = new();

    public IInterceptor[] Interceptors { get; }

    public PerceptualHashIndex()
    {
        Interceptors = [new SaveWatcher(this), new CommitWatcher(this)];
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

    private static bool TouchesHashes(DbContext context) =>
        context.ChangeTracker.Entries<Post>().Any(e =>
            e.State is EntityState.Added or EntityState.Deleted
            || (e.State == EntityState.Modified && e.Property(p => p.PerceptualHash).IsModified));

    /// <summary>
    /// Invalidates once the change is visible to other connections: after the save, or
    /// after the commit when the save ran inside an explicit transaction.
    /// </summary>
    private void Saved(DbContext context)
    {
        if (!pending.TryGetValue(context, out _)) return;
        if (context.Database.CurrentTransaction is not null) return;

        pending.Remove(context);
        Invalidate();
    }

    private void Ended(DbContext? context)
    {
        if (context is null || !pending.Remove(context)) return;
        Invalidate();
    }

    private sealed class SaveWatcher(PerceptualHashIndex index) : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result)
        {
            Track(eventData.Context);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            Track(eventData.Context);
            return ValueTask.FromResult(result);
        }

        public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
        {
            if (eventData.Context is { } context) index.Saved(context);
            return result;
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
        {
            if (eventData.Context is { } context) index.Saved(context);
            return ValueTask.FromResult(result);
        }

        private void Track(DbContext? context)
        {
            if (context is not null && TouchesHashes(context)) index.pending.AddOrUpdate(context, index);
        }
    }

    private sealed class CommitWatcher(PerceptualHashIndex index) : DbTransactionInterceptor
    {
        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
            index.Ended(eventData.Context);

        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
        {
            index.Ended(eventData.Context);
            return Task.CompletedTask;
        }

        public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
            index.Ended(eventData.Context);

        public override Task TransactionRolledBackAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
        {
            index.Ended(eventData.Context);
            return Task.CompletedTask;
        }
    }
}
