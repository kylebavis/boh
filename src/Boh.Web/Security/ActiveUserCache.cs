using System.Collections.Concurrent;
using Boh.Web.Data;
using Boh.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Boh.Web.Security;

/// <summary>
/// Signed-in users as <see cref="RevalidateUserEvents"/> last read them, so a page of
/// thumbnails is not a user lookup per image.
/// </summary>
/// <remarks>
/// An entry is dropped as soon as EF commits a change to that user, so deleting or demoting
/// someone still applies on their next request. The lifetime only bounds a change made
/// outside EF.
/// </remarks>
public sealed class ActiveUserCache
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<int, (User User, long ExpiresAt)> entries = new();
    private readonly TimeProvider time;
    private readonly Lock gate = new();
    private long generation;

    public IInterceptor[] Interceptors { get; }

    public ActiveUserCache(TimeProvider? time = null)
    {
        this.time = time ?? TimeProvider.System;
        Interceptors = new CommittedChanges<int>(ChangedUsers, Invalidate).Interceptors;
    }

    /// <summary>The user, or null when they no longer exist. Not cached when missing.</summary>
    public async Task<User?> GetAsync(int userId, Func<Task<User?>> load)
    {
        var now = time.GetTimestamp();
        if (entries.TryGetValue(userId, out var entry) && now < entry.ExpiresAt) return entry.User;

        long started;
        lock (gate) started = generation;

        var user = await load();
        if (user is null) return null;

        // A change committed during the load may be missing from it, so it is only kept if
        // nothing was invalidated in the meantime.
        lock (gate)
        {
            if (generation == started)
            {
                entries[userId] = (user, now + (long)(Lifetime.TotalSeconds * time.TimestampFrequency));
            }
        }

        return user;
    }

    public void Invalidate(IReadOnlyCollection<int> userIds)
    {
        lock (gate)
        {
            generation++;
            foreach (var id in userIds) entries.TryRemove(id, out _);
        }
    }

    private static IEnumerable<int> ChangedUsers(DbContext context) =>
        context.ChangeTracker.Entries<User>()
            .Where(e => e.State is EntityState.Modified or EntityState.Deleted)
            .Select(e => e.Entity.Id);
}
