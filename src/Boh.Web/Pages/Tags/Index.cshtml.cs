using Boh.Web.Data;
using Boh.Web.Services;
using Boh.Web.Tags;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Boh.Web.Pages.Tags;

public sealed record TagRow(
    int Id,
    string Display,
    string Namespace,
    int PostCount,
    string? AliasOf,
    IReadOnlyList<string> Implies,
    string? Color);

public class IndexModel(BohDbContext db, TagService tags, BohOptions options) : PageModel
{
    public bool AuthDisabled => options.AuthDisabled;

    private const int PageSize = 200;

    public IReadOnlyList<TagRow> Rows { get; private set; } = [];
    public string? Filter { get; private set; }
    public int TotalCount { get; private set; }

    public async Task OnGetAsync(string? filter, CancellationToken ct)
    {
        Filter = filter;

        var query = db.Tags.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(filter))
        {
            var needle = filter.Trim().ToLowerInvariant();
            query = query.Where(t => t.Name.Contains(needle) || t.Namespace.Contains(needle));
        }

        TotalCount = await query.CountAsync(ct);

        var rows = await query
            .OrderByDescending(t => t.PostCount)
            .ThenBy(t => t.Namespace)
            .ThenBy(t => t.Name)
            .Take(PageSize)
            .Select(t => new { t.Id, t.Namespace, t.Name, t.PostCount })
            .ToListAsync(ct);

        var namespaceColors = await tags.GetNamespaceColorsAsync(ct);
        var ids = rows.Select(t => t.Id).ToList();

        var aliases = await db.TagAliases.AsNoTracking()
            .Where(a => ids.Contains(a.AliasTagId))
            .Select(a => new { a.AliasTagId, a.CanonicalTag.Namespace, a.CanonicalTag.Name })
            .ToDictionaryAsync(a => a.AliasTagId, a => new TagName(a.Namespace, a.Name).Display, ct);

        var implications = (await db.TagImplications.AsNoTracking()
                .Where(i => ids.Contains(i.ChildTagId))
                .Select(i => new { i.ChildTagId, i.ParentTag.Namespace, i.ParentTag.Name })
                .ToListAsync(ct))
            .ToLookup(i => i.ChildTagId, i => new TagName(i.Namespace, i.Name).Display);

        Rows = rows
            .Select(t => new TagRow(
                t.Id,
                new TagName(t.Namespace, t.Name).Display,
                t.Namespace,
                t.PostCount,
                aliases.GetValueOrDefault(t.Id),
                implications[t.Id].OrderBy(x => x, StringComparer.Ordinal).ToList(),
                NamespacePalette.ColorFor(t.Namespace, namespaceColors)))
            .ToList();
    }
}
