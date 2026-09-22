using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Boh.Web.Data;

/// <summary>
/// Reports the keys a save touched once the change is visible to other connections: after
/// the save, or after the commit when the save ran inside an explicit transaction. For
/// caches that must not reload before the change lands.
/// </summary>
/// <remarks>Bulk <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> bypasses this.</remarks>
public sealed class CommittedChanges<TKey>
{
    private readonly Func<DbContext, IEnumerable<TKey>> collect;
    private readonly Action<IReadOnlyCollection<TKey>> committed;

    // Keys saved but not yet committed, per context.
    private readonly ConditionalWeakTable<DbContext, HashSet<TKey>> pending = new();

    public IInterceptor[] Interceptors { get; }

    /// <param name="collect">Keys of interest among the context's pending changes.</param>
    /// <param name="committed">Called with the keys once they are committed.</param>
    public CommittedChanges(
        Func<DbContext, IEnumerable<TKey>> collect, Action<IReadOnlyCollection<TKey>> committed)
    {
        this.collect = collect;
        this.committed = committed;
        Interceptors = [new SaveWatcher(this), new CommitWatcher(this)];
    }

    private void Saving(DbContext? context)
    {
        if (context is null) return;

        var keys = collect(context).ToList();
        if (keys.Count == 0) return;

        lock (pending)
        {
            pending.GetOrCreateValue(context).UnionWith(keys);
        }
    }

    private void Saved(DbContext? context)
    {
        if (context?.Database.CurrentTransaction is null) Flush(context);
    }

    private void Flush(DbContext? context)
    {
        if (context is null) return;

        HashSet<TKey>? keys;
        lock (pending)
        {
            if (!pending.TryGetValue(context, out keys)) return;
            pending.Remove(context);
        }

        committed(keys);
    }

    private sealed class SaveWatcher(CommittedChanges<TKey> owner) : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result)
        {
            owner.Saving(eventData.Context);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            owner.Saving(eventData.Context);
            return ValueTask.FromResult(result);
        }

        public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
        {
            owner.Saved(eventData.Context);
            return result;
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
        {
            owner.Saved(eventData.Context);
            return ValueTask.FromResult(result);
        }
    }

    // Rollbacks flush too: reloading after a change that did not happen is harmless.
    private sealed class CommitWatcher(CommittedChanges<TKey> owner) : DbTransactionInterceptor
    {
        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
            owner.Flush(eventData.Context);

        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
        {
            owner.Flush(eventData.Context);
            return Task.CompletedTask;
        }

        public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
            owner.Flush(eventData.Context);

        public override Task TransactionRolledBackAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
        {
            owner.Flush(eventData.Context);
            return Task.CompletedTask;
        }
    }
}
