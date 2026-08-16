using Boh.Web.Data;
using Boh.Web.Data.Entities;
using Boh.Web.Tags;
using Microsoft.EntityFrameworkCore;

namespace Boh.Web.Services;

public sealed record TagSuggestion(string Display, int PostCount, string? AliasOf, string? Color);

/// <summary>
/// Search terms already resolved to canonical tag ids. <paramref name="Unsatisfiable"/> is
/// set when a required tag does not exist at all, in which case no post can match and the
/// caller should skip querying entirely.
/// </summary>
public sealed record ResolvedSearch(IReadOnlyList<int> Include, IReadOnlyList<int> Exclude, bool Unsatisfiable);

public abstract record TagLinkResult
{
    private TagLinkResult() { }

    public sealed record Ok : TagLinkResult;
    public sealed record Rejected(string Reason) : TagLinkResult;
}

/// <summary>
/// Owns every write to the tag graph. Aliases are resolved before storage so an aliased
/// tag never lands on a post, and implied tags are materialized into <see cref="PostTag"/>
/// rows so search stays a plain indexed join.
/// </summary>
/// <remarks>
/// The alias map and implication graph are loaded into memory to compute closures. At the
/// scale this project targets — a personal collection, hundreds of implications — that is
/// far simpler than recursive CTEs and costs one small query. It would need revisiting for
/// a graph with tens of thousands of edges.
/// </remarks>
public sealed class TagService(BohDbContext db, ILogger<TagService> logger)
{
    /// <summary>Stops a malformed alias chain from looping forever.</summary>
    private const int MaxAliasDepth = 16;

    // ---- reads ---------------------------------------------------------

    public Task<Tag?> FindAsync(TagName name, CancellationToken ct) =>
        db.Tags.FirstOrDefaultAsync(t => t.Namespace == name.Namespace && t.Name == name.Name, ct);

    public async Task<List<TagName>> GetExplicitTagNamesAsync(int postId, CancellationToken ct) =>
        (await db.PostTags.AsNoTracking()
            .Where(pt => pt.PostId == postId && pt.Source == TagSource.Explicit)
            .Select(pt => new { pt.Tag.Namespace, pt.Tag.Name })
            .ToListAsync(ct))
        .Select(t => new TagName(t.Namespace, t.Name))
        .ToList();

    public async Task<List<TagSuggestion>> AutocompleteAsync(string? prefix, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return [];

        var raw = prefix.Trim().ToLowerInvariant();
        var colon = raw.IndexOf(':');

        IQueryable<Tag> query = db.Tags.AsNoTracking();

        if (colon > 0)
        {
            var ns = raw[..colon];
            var namePrefix = raw[(colon + 1)..];
            query = query.Where(t => t.Namespace == ns && t.Name.StartsWith(namePrefix));
        }
        else
        {
            // Bare input can be completing either half, so offer both.
            query = query.Where(t => t.Name.StartsWith(raw) || t.Namespace.StartsWith(raw));
        }

        var matches = await query
            .OrderByDescending(t => t.PostCount)
            .ThenBy(t => t.Namespace)
            .ThenBy(t => t.Name)
            .Take(limit)
            .Select(t => new { t.Id, t.Namespace, t.Name, t.PostCount })
            .ToListAsync(ct);

        if (matches.Count == 0) return [];

        // Surface aliases so the user learns the canonical name instead of picking a dead end.
        var ids = matches.Select(m => m.Id).ToList();
        var aliases = await db.TagAliases.AsNoTracking()
            .Where(a => ids.Contains(a.AliasTagId))
            .Select(a => new { a.AliasTagId, a.CanonicalTag.Namespace, a.CanonicalTag.Name })
            .ToDictionaryAsync(
                a => a.AliasTagId,
                a => new TagName(a.Namespace, a.Name).Display,
                ct);

        var namespaceColors = await GetNamespaceColorsAsync(ct);

        return matches
            .Select(m => new TagSuggestion(
                new TagName(m.Namespace, m.Name).Display,
                m.PostCount,
                aliases.GetValueOrDefault(m.Id),
                NamespacePalette.ColorFor(m.Namespace, namespaceColors)))
            .ToList();
    }

    public async Task<ResolvedSearch> ResolveSearchAsync(SearchQuery query, CancellationToken ct)
    {
        if (query.IsEmpty) return new ResolvedSearch([], [], false);

        var aliasMap = await LoadAliasMapAsync(ct);
        var found = await LookupManyAsync(query.TagTerms.Select(t => t.Tag).ToList(), ct);

        var include = new List<int>();
        var exclude = new List<int>();

        foreach (var term in query.TagTerms)
        {
            if (!found.TryGetValue(term.Tag, out var tag))
            {
                // Requiring a tag nobody has means nothing matches; excluding one is a no-op.
                if (!term.Exclude) return new ResolvedSearch([], [], true);
                continue;
            }

            var canonical = ResolveAlias(aliasMap, tag.Id);
            (term.Exclude ? exclude : include).Add(canonical);
        }

        return new ResolvedSearch(include.Distinct().ToList(), exclude.Distinct().ToList(), false);
    }

    // ---- post tagging --------------------------------------------------

    /// <summary>
    /// Replaces a post's explicit tags and recomputes its implied ones. Implied rows that
    /// are no longer justified by the remaining explicit tags are dropped, which is only
    /// decidable because <see cref="PostTag.Source"/> records how each row got there.
    /// </summary>
    public async Task SetPostTagsAsync(int postId, IReadOnlyCollection<TagName> names, CancellationToken ct)
    {
        var aliasMap = await LoadAliasMapAsync(ct);
        var implications = await LoadImplicationsAsync(ct);

        var tags = await GetOrCreateManyAsync(names, ct);

        var explicitIds = tags.Values.Select(t => ResolveAlias(aliasMap, t.Id)).ToHashSet();
        var impliedIds = AncestorsOf(implications, aliasMap, explicitIds);

        var desired = new Dictionary<int, TagSource>();
        foreach (var id in explicitIds) desired[id] = TagSource.Explicit;
        foreach (var id in impliedIds) desired[id] = TagSource.Implied;

        await ApplyDesiredLinksAsync(postId, desired, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Adds tags to a post, keeping everything already there.</summary>
    public async Task AddPostTagsAsync(int postId, IReadOnlyCollection<TagName> names, CancellationToken ct)
    {
        var current = await GetExplicitTagNamesAsync(postId, ct);
        await SetPostTagsAsync(postId, current.Concat(names).Distinct().ToList(), ct);
    }

    public async Task RemovePostTagAsync(int postId, TagName name, CancellationToken ct)
    {
        var current = await GetExplicitTagNamesAsync(postId, ct);
        await SetPostTagsAsync(postId, current.Where(t => t != name).ToList(), ct);
    }

    // ---- aliases -------------------------------------------------------

    public async Task<TagLinkResult> AddAliasAsync(TagName aliasName, TagName canonicalName, CancellationToken ct)
    {
        if (aliasName == canonicalName)
            return new TagLinkResult.Rejected("A tag cannot be an alias of itself.");

        var tags = await GetOrCreateManyAsync([aliasName, canonicalName], ct);
        var alias = tags[aliasName];
        var canonical = tags[canonicalName];

        var aliasMap = await LoadAliasMapAsync(ct);

        if (ResolveAlias(aliasMap, canonical.Id) == alias.Id)
            return new TagLinkResult.Rejected(
                $"'{canonicalName.Display}' already resolves to '{aliasName.Display}'; that would form a loop.");

        if (await db.TagAliases.AnyAsync(a => a.AliasTagId == alias.Id, ct))
            return new TagLinkResult.Rejected($"'{aliasName.Display}' is already an alias.");

        db.TagAliases.Add(new TagAlias { AliasTagId = alias.Id, CanonicalTagId = canonical.Id });
        await db.SaveChangesAsync(ct);

        // With the alias in place, re-applying each affected post's explicit tags moves
        // them onto the canonical tag through the ordinary tagging path.
        var affected = await db.PostTags.AsNoTracking()
            .Where(pt => pt.TagId == alias.Id)
            .Select(pt => pt.PostId)
            .Distinct()
            .ToListAsync(ct);

        foreach (var postId in affected)
        {
            var explicitNames = await GetExplicitTagNamesAsync(postId, ct);
            await SetPostTagsAsync(postId, explicitNames, ct);
        }

        logger.LogInformation("Aliased {Alias} to {Canonical}, migrating {Count} post(s)",
            aliasName.Display, canonicalName.Display, affected.Count);

        return new TagLinkResult.Ok();
    }

    public async Task RemoveAliasAsync(int aliasTagId, CancellationToken ct)
    {
        await db.TagAliases.Where(a => a.AliasTagId == aliasTagId).ExecuteDeleteAsync(ct);
    }

    // ---- implications --------------------------------------------------

    public async Task<TagLinkResult> AddImplicationAsync(TagName childName, TagName parentName, CancellationToken ct)
    {
        if (childName == parentName)
            return new TagLinkResult.Rejected("A tag cannot imply itself.");

        var tags = await GetOrCreateManyAsync([childName, parentName], ct);
        var aliasMap = await LoadAliasMapAsync(ct);

        var childId = ResolveAlias(aliasMap, tags[childName].Id);
        var parentId = ResolveAlias(aliasMap, tags[parentName].Id);

        if (childId == parentId)
            return new TagLinkResult.Rejected("Those tags resolve to the same tag through an alias.");

        var implications = await LoadImplicationsAsync(ct);

        // If the parent already reaches the child, adding this edge closes a loop and the
        // closure walk would never terminate on its own.
        if (AncestorsOf(implications, aliasMap, [parentId]).Contains(childId))
            return new TagLinkResult.Rejected(
                $"'{parentName.Display}' already implies '{childName.Display}'; that would form a cycle.");

        if (await db.TagImplications.AnyAsync(i => i.ChildTagId == childId && i.ParentTagId == parentId, ct))
            return new TagLinkResult.Rejected("That implication already exists.");

        db.TagImplications.Add(new TagImplication { ChildTagId = childId, ParentTagId = parentId });
        await db.SaveChangesAsync(ct);

        await RebuildImpliedForTagAsync(childId, ct);
        return new TagLinkResult.Ok();
    }

    public async Task RemoveImplicationAsync(int childTagId, int parentTagId, CancellationToken ct)
    {
        await db.TagImplications
            .Where(i => i.ChildTagId == childTagId && i.ParentTagId == parentTagId)
            .ExecuteDeleteAsync(ct);

        await RebuildImpliedForTagAsync(childTagId, ct);
    }

    // ---- maintenance ---------------------------------------------------

    /// <summary>
    /// Recomputes the implied tags of every post. Exposed for the admin "rebuild" action,
    /// which is the repair-everything path; routine implication edits use the scoped
    /// rebuild instead. Also recounts tag totals, since a full pass is the natural place
    /// to correct any drift.
    /// </summary>
    public async Task<int> RebuildAllImpliedAsync(CancellationToken ct)
    {
        var changes = await RebuildImpliedAsync(null, ct);
        await RecountTagsAsync(ct);

        logger.LogInformation("Rebuilt implied tags for all posts: {Changes} link(s) changed", changes);
        return changes;
    }

    /// <summary>
    /// Recomputes implied tags only for posts carrying <paramref name="tagId"/>.
    /// </summary>
    /// <remarks>
    /// Adding or removing the edge <c>tag -&gt; parent</c> can only change the closure of a
    /// post whose closure already contains <c>tag</c>. Because closures are materialized,
    /// every such post has a PostTags row for it — implied rows included — so this single
    /// indexed lookup finds them all. Recomputing the rest of the collection would be
    /// wasted work.
    /// </remarks>
    private async Task<int> RebuildImpliedForTagAsync(int tagId, CancellationToken ct)
    {
        var postIds = await db.PostTags.AsNoTracking()
            .Where(pt => pt.TagId == tagId)
            .Select(pt => pt.PostId)
            .Distinct()
            .ToListAsync(ct);

        if (postIds.Count == 0) return 0;

        var changes = await RebuildImpliedAsync(postIds, ct);

        logger.LogInformation("Rebuilt implied tags for {Posts} post(s): {Changes} link(s) changed",
            postIds.Count, changes);

        return changes;
    }

    /// <summary>
    /// Shared rebuild core. A null <paramref name="postIds"/> means every post.
    /// Counts are adjusted from the same change set so they land in one SaveChanges.
    /// </summary>
    private async Task<int> RebuildImpliedAsync(IReadOnlyCollection<int>? postIds, CancellationToken ct)
    {
        var implications = await LoadImplicationsAsync(ct);
        var aliasMap = await LoadAliasMapAsync(ct);

        var query = db.PostTags.AsQueryable();
        if (postIds is not null) query = query.Where(pt => postIds.Contains(pt.PostId));

        var rows = await query.ToListAsync(ct);

        var added = new List<int>();
        var removed = new List<int>();

        foreach (var group in rows.GroupBy(r => r.PostId))
        {
            var explicitIds = group.Where(r => r.Source == TagSource.Explicit)
                                   .Select(r => r.TagId)
                                   .ToHashSet();

            var desiredImplied = AncestorsOf(implications, aliasMap, explicitIds);
            var currentImplied = group.Where(r => r.Source == TagSource.Implied).ToList();

            foreach (var row in currentImplied.Where(r => !desiredImplied.Contains(r.TagId)))
            {
                db.PostTags.Remove(row);
                removed.Add(row.TagId);
            }

            var alreadyImplied = currentImplied.Select(r => r.TagId).ToHashSet();
            foreach (var tagId in desiredImplied.Where(id => !alreadyImplied.Contains(id)))
            {
                db.PostTags.Add(new PostTag { PostId = group.Key, TagId = tagId, Source = TagSource.Implied });
                added.Add(tagId);
            }
        }

        await AdjustPostCountsAsync(added, removed, ct);
        await db.SaveChangesAsync(ct);

        return added.Count + removed.Count;
    }

    // ---- renaming and namespacing --------------------------------------

    /// <summary>
    /// Renames a tag, which is also how a tag is moved into a namespace — <c>foo</c> to
    /// <c>artist:foo</c> is just a rename. Merges into the destination when it already exists.
    /// </summary>
    /// <remarks>
    /// When nothing occupies the destination this is a single column update, which keeps
    /// every post link, alias and implication pointing at the same row automatically.
    /// A merge is the harder path: links must be repointed without duplicating the primary
    /// key, and an explicit link must never be demoted to implied just because the
    /// destination happened to hold an implied one.
    /// </remarks>
    public async Task<TagLinkResult> MoveTagAsync(TagName from, TagName to, CancellationToken ct)
    {
        if (from == to) return new TagLinkResult.Rejected("Those are the same tag.");

        var source = await FindAsync(from, ct);
        if (source is null) return new TagLinkResult.Rejected($"No tag called '{from.Display}'.");

        var destination = await FindAsync(to, ct);

        if (destination is null)
        {
            source.Namespace = to.Namespace;
            source.Name = to.Name;
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Renamed tag {From} to {To}", from.Display, to.Display);
            return new TagLinkResult.Ok();
        }

        await MergeTagsAsync(source, destination, ct);

        logger.LogInformation("Merged tag {From} into existing {To}", from.Display, to.Display);
        return new TagLinkResult.Ok();
    }

    private async Task MergeTagsAsync(Tag source, Tag destination, CancellationToken ct)
    {
        var sourceLinks = await db.PostTags.Where(pt => pt.TagId == source.Id).ToListAsync(ct);
        var postIds = sourceLinks.Select(pt => pt.PostId).ToList();

        var destinationLinks = await db.PostTags
            .Where(pt => pt.TagId == destination.Id && postIds.Contains(pt.PostId))
            .ToListAsync(ct);

        var destinationByPost = destinationLinks.ToDictionary(pt => pt.PostId);

        foreach (var link in sourceLinks)
        {
            if (destinationByPost.TryGetValue(link.PostId, out var existing))
            {
                // Both tags on one post: keep the stronger provenance, drop the duplicate.
                if (link.Source == TagSource.Explicit) existing.Source = TagSource.Explicit;
                db.PostTags.Remove(link);
            }
            else
            {
                // TagId is part of the primary key, so the row is replaced rather than updated.
                db.PostTags.Remove(link);
                db.PostTags.Add(new PostTag
                {
                    PostId = link.PostId,
                    TagId = destination.Id,
                    Source = link.Source
                });
            }
        }

        await db.SaveChangesAsync(ct);

        // Alias and implication rows reference the source by id and would cascade away with
        // it, silently discarding rules the operator configured. Repoint them first, then
        // drop anything that has become self-referential.
        await db.TagAliases.Where(a => a.AliasTagId == source.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.AliasTagId, destination.Id), ct);
        await db.TagAliases.Where(a => a.CanonicalTagId == source.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.CanonicalTagId, destination.Id), ct);
        await db.TagAliases.Where(a => a.AliasTagId == a.CanonicalTagId).ExecuteDeleteAsync(ct);

        await db.TagImplications.Where(i => i.ChildTagId == source.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.ChildTagId, destination.Id), ct);
        await db.TagImplications.Where(i => i.ParentTagId == source.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.ParentTagId, destination.Id), ct);
        await db.TagImplications.Where(i => i.ChildTagId == i.ParentTagId).ExecuteDeleteAsync(ct);

        db.ChangeTracker.Clear();
        await db.Tags.Where(t => t.Id == source.Id).ExecuteDeleteAsync(ct);

        await RecountTagsAsync(ct);
    }

    // ---- namespaces ----------------------------------------------------

    /// <summary>Explicit colour overrides, keyed by namespace.</summary>
    public async Task<Dictionary<string, string>> GetNamespaceColorsAsync(CancellationToken ct) =>
        await db.TagNamespaces.AsNoTracking().ToDictionaryAsync(n => n.Name, n => n.Color, ct);

    /// <summary>Every namespace currently in use, whether or not it has been styled.</summary>
    public async Task<List<string>> GetUsedNamespacesAsync(CancellationToken ct) =>
        await db.Tags.AsNoTracking()
            .Where(t => t.Namespace != "")
            .Select(t => t.Namespace)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync(ct);

    public async Task<TagLinkResult> SetNamespaceColorAsync(string? ns, string? color, CancellationToken ct)
    {
        var name = ns?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(name)) return new TagLinkResult.Rejected("Enter a namespace.");

        var normalized = NamespacePalette.Normalize(color);
        if (normalized is null)
            return new TagLinkResult.Rejected("Enter a colour as hex, for example #a371f7.");

        var existing = await db.TagNamespaces.FirstOrDefaultAsync(n => n.Name == name, ct);
        if (existing is null) db.TagNamespaces.Add(new TagNamespace { Name = name, Color = normalized });
        else existing.Color = normalized;

        await db.SaveChangesAsync(ct);
        return new TagLinkResult.Ok();
    }

    /// <summary>Drops an override so the namespace falls back to its palette colour.</summary>
    public async Task ResetNamespaceColorAsync(string ns, CancellationToken ct) =>
        await db.TagNamespaces.Where(n => n.Name == ns).ExecuteDeleteAsync(ct);

    /// <summary>
    /// Deletes tags that no post carries and that no alias or implication refers to.
    /// Returns how many rows went.
    /// </summary>
    /// <remarks>
    /// Two things make this less obvious than "PostCount == 0".
    /// <para>
    /// Usage is read from the link table rather than the denormalized counter, because that
    /// counter can drift — repairing it is what <see cref="RecountTagsAsync"/> is for, and a
    /// destructive operation should not trust a value it is able to check directly.
    /// </para>
    /// <para>
    /// Alias and implication rows cascade when their tag is deleted, and an alias tag holds
    /// a count of zero by design so that it stays discoverable. Deleting on count alone
    /// would therefore wipe every alias and implication the operator had configured, with
    /// no error — aliases would simply stop redirecting. Configuration counts as a reason
    /// to keep a tag, even with nothing tagged.
    /// </para>
    /// </remarks>
    public async Task<int> DeleteUnusedTagsAsync(CancellationToken ct)
    {
        var deleted = await db.Tags
            .Where(t => !db.PostTags.Any(pt => pt.TagId == t.Id)
                     && !db.TagAliases.Any(a => a.AliasTagId == t.Id || a.CanonicalTagId == t.Id)
                     && !db.TagImplications.Any(i => i.ChildTagId == t.Id || i.ParentTagId == t.Id))
            .ExecuteDeleteAsync(ct);

        logger.LogInformation("Deleted {Count} unused tag(s)", deleted);
        return deleted;
    }

    /// <summary>Repairs <see cref="Tag.PostCount"/> drift by recounting from the link table.</summary>
    public async Task RecountTagsAsync(CancellationToken ct)
    {
        var counts = await db.PostTags.AsNoTracking()
            .GroupBy(pt => pt.TagId)
            .Select(g => new { TagId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TagId, x => x.Count, ct);

        var tags = await db.Tags.ToListAsync(ct);
        foreach (var tag in tags) tag.PostCount = counts.GetValueOrDefault(tag.Id, 0);

        await db.SaveChangesAsync(ct);
    }

    // ---- internals -----------------------------------------------------

    private async Task ApplyDesiredLinksAsync(
        int postId, Dictionary<int, TagSource> desired, CancellationToken ct)
    {
        var existing = await db.PostTags.Where(pt => pt.PostId == postId).ToListAsync(ct);
        var existingByTag = existing.ToDictionary(pt => pt.TagId);

        var added = new List<int>();
        var removed = new List<int>();

        foreach (var (tagId, source) in desired)
        {
            if (existingByTag.TryGetValue(tagId, out var row))
            {
                row.Source = source;
            }
            else
            {
                db.PostTags.Add(new PostTag { PostId = postId, TagId = tagId, Source = source });
                added.Add(tagId);
            }
        }

        foreach (var row in existing.Where(r => !desired.ContainsKey(r.TagId)))
        {
            db.PostTags.Remove(row);
            removed.Add(row.TagId);
        }

        await AdjustPostCountsAsync(added, removed, ct);
    }

    /// <summary>
    /// Adjusts counts on tracked entities so they land in the same SaveChanges as the link
    /// changes; an ExecuteUpdate here would commit separately and drift on failure.
    /// </summary>
    private async Task AdjustPostCountsAsync(List<int> added, List<int> removed, CancellationToken ct)
    {
        var affected = added.Concat(removed).Distinct().ToList();
        if (affected.Count == 0) return;

        var tags = await db.Tags.Where(t => affected.Contains(t.Id)).ToListAsync(ct);
        var byId = tags.ToDictionary(t => t.Id);

        foreach (var id in added)
        {
            if (byId.TryGetValue(id, out var tag)) tag.PostCount++;
        }

        foreach (var id in removed)
        {
            if (byId.TryGetValue(id, out var tag)) tag.PostCount = Math.Max(0, tag.PostCount - 1);
        }
    }

    private async Task<Dictionary<TagName, Tag>> LookupManyAsync(
        IReadOnlyCollection<TagName> names, CancellationToken ct)
    {
        if (names.Count == 0) return [];

        // EF cannot translate a Contains over (namespace, name) pairs, so filter on the
        // name column — which is indexed and highly selective — and pair up in memory.
        var candidateNames = names.Select(n => n.Name).Distinct().ToList();
        var candidates = await db.Tags
            .Where(t => candidateNames.Contains(t.Name))
            .ToListAsync(ct);

        var wanted = names.ToHashSet();
        return candidates
            .Where(t => wanted.Contains(new TagName(t.Namespace, t.Name)))
            .ToDictionary(t => new TagName(t.Namespace, t.Name));
    }

    private async Task<Dictionary<TagName, Tag>> GetOrCreateManyAsync(
        IReadOnlyCollection<TagName> names, CancellationToken ct)
    {
        var result = await LookupManyAsync(names, ct);

        var missing = names.Distinct().Where(n => !result.ContainsKey(n)).ToList();
        if (missing.Count == 0) return result;

        foreach (var name in missing)
        {
            var tag = new Tag { Namespace = name.Namespace, Name = name.Name };
            db.Tags.Add(tag);
            result[name] = tag;
        }

        // Needs its own save so the new rows have ids before links reference them.
        await db.SaveChangesAsync(ct);
        return result;
    }

    private async Task<Dictionary<int, int>> LoadAliasMapAsync(CancellationToken ct) =>
        await db.TagAliases.AsNoTracking()
            .ToDictionaryAsync(a => a.AliasTagId, a => a.CanonicalTagId, ct);

    private async Task<ILookup<int, int>> LoadImplicationsAsync(CancellationToken ct)
    {
        var rows = await db.TagImplications.AsNoTracking()
            .Select(i => new { i.ChildTagId, i.ParentTagId })
            .ToListAsync(ct);

        return rows.ToLookup(r => r.ChildTagId, r => r.ParentTagId);
    }

    private static int ResolveAlias(Dictionary<int, int> aliasMap, int tagId)
    {
        var current = tagId;

        for (var depth = 0; depth < MaxAliasDepth; depth++)
        {
            if (!aliasMap.TryGetValue(current, out var next) || next == current) break;
            current = next;
        }

        return current;
    }

    /// <summary>
    /// Every tag transitively implied by <paramref name="seeds"/>, excluding the seeds
    /// themselves — a seed that is also an ancestor stays explicit rather than being
    /// demoted to implied.
    /// </summary>
    /// <remarks>
    /// Parents are resolved through <paramref name="aliasMap"/> on the way out. An
    /// implication is stored canonically when it is created, but aliasing a tag afterwards
    /// leaves every existing edge pointing at what is now an alias — and an implied row is
    /// the one way an aliased tag could still reach a post. The alias's own edges are walked
    /// as well, since they remain real implications after the redirect.
    /// </remarks>
    private static HashSet<int> AncestorsOf(
        ILookup<int, int> childToParents, Dictionary<int, int> aliasMap, IReadOnlyCollection<int> seeds)
    {
        var ancestors = new HashSet<int>();
        var visited = new HashSet<int>(seeds);
        var queue = new Queue<int>(seeds);

        while (queue.Count > 0)
        {
            foreach (var parent in childToParents[queue.Dequeue()])
            {
                var canonical = ResolveAlias(aliasMap, parent);

                // Follow the alias's edges without letting the alias itself become implied.
                if (canonical != parent && visited.Add(parent)) queue.Enqueue(parent);

                if (!visited.Add(canonical)) continue;
                ancestors.Add(canonical);
                queue.Enqueue(canonical);
            }
        }

        return ancestors;
    }
}
