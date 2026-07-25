using Boh.Web.Data;
using Boh.Web.Services;
using Boh.Web.Tags;
using Boh.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Boh.Web.Pages.Tags;

public sealed record AliasRow(int AliasTagId, string Alias, string Canonical);
public sealed record ImplicationRow(int ChildTagId, int ParentTagId, string Child, string Parent);

/// <summary>
/// <paramref name="IsDefault"/> distinguishes a colour picked from the palette from one the
/// operator set, so the UI can offer to reset only what was actually overridden.
/// </summary>
public sealed record NamespaceRow(string Name, string Color, bool IsDefault, int TagCount);

/// <summary>Authorized explicitly so it stays private even when browsing is public.</summary>
[Authorize(Policy = BohPolicies.IsAdmin)]
public class AdminModel(BohDbContext db, TagService tags) : PageModel
{
    public IReadOnlyList<AliasRow> Aliases { get; private set; } = [];
    public IReadOnlyList<ImplicationRow> Implications { get; private set; } = [];
    public IReadOnlyList<NamespaceRow> Namespaces { get; private set; } = [];

    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostAddAliasAsync(string? alias, string? canonical, CancellationToken ct)
    {
        if (!TagName.TryParse(alias, out var aliasName) || !TagName.TryParse(canonical, out var canonicalName))
        {
            Error = "Both fields need a usable tag name.";
            return RedirectToPage();
        }

        Apply(await tags.AddAliasAsync(aliasName, canonicalName, ct),
            $"'{aliasName.Display}' now redirects to '{canonicalName.Display}'.");

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAliasAsync(int aliasTagId, CancellationToken ct)
    {
        await tags.RemoveAliasAsync(aliasTagId, ct);
        Message = "Alias removed.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddImplicationAsync(string? child, string? parent, CancellationToken ct)
    {
        if (!TagName.TryParse(child, out var childName) || !TagName.TryParse(parent, out var parentName))
        {
            Error = "Both fields need a usable tag name.";
            return RedirectToPage();
        }

        Apply(await tags.AddImplicationAsync(childName, parentName, ct),
            $"'{childName.Display}' now implies '{parentName.Display}'.");

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveImplicationAsync(int childTagId, int parentTagId, CancellationToken ct)
    {
        await tags.RemoveImplicationAsync(childTagId, parentTagId, ct);
        Message = "Implication removed; implied tags rebuilt.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSetNamespaceColorAsync(string? ns, string? color, CancellationToken ct)
    {
        Apply(await tags.SetNamespaceColorAsync(ns, color, ct), $"Colour set for '{ns}'.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetNamespaceColorAsync(string ns, CancellationToken ct)
    {
        await tags.ResetNamespaceColorAsync(ns, ct);
        Message = $"'{ns}' is back to its default colour.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostMoveTagAsync(string? from, string? to, CancellationToken ct)
    {
        if (!TagName.TryParse(from, out var fromName) || !TagName.TryParse(to, out var toName))
        {
            Error = "Both fields need a usable tag name.";
            return RedirectToPage();
        }

        Apply(await tags.MoveTagAsync(fromName, toName, ct),
            $"'{fromName.Display}' is now '{toName.Display}'.");

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRebuildAsync(CancellationToken ct)
    {
        var changes = await tags.RebuildAllImpliedAsync(ct);
        Message = $"Rebuilt implied tags — {changes} link(s) changed.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRecountAsync(CancellationToken ct)
    {
        await tags.RecountTagsAsync(ct);
        Message = "Tag post counts recomputed.";
        return RedirectToPage();
    }

    private void Apply(TagLinkResult result, string successMessage)
    {
        if (result is TagLinkResult.Rejected rejected) Error = rejected.Reason;
        else Message = successMessage;
    }

    /// <summary>
    /// Projects plain columns and assembles the display strings in memory. Building
    /// "namespace:name" inside the query and then ordering by that computed value is not
    /// translatable, and these tables are small enough that shaping client-side costs nothing.
    /// </summary>
    private async Task LoadNamespacesAsync(CancellationToken ct)
    {
        var overrides = await tags.GetNamespaceColorsAsync(ct);

        var counts = await db.Tags.AsNoTracking()
            .Where(t => t.Namespace != "")
            .GroupBy(t => t.Namespace)
            .Select(g => new { Namespace = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Namespace, x => x.Count, ct);

        // Namespaces in use, plus any styled but currently unused, so a colour set ahead of
        // time does not vanish from the list.
        var names = counts.Keys.Concat(overrides.Keys).Distinct().OrderBy(n => n, StringComparer.Ordinal);

        Namespaces = names
            .Select(n => new NamespaceRow(
                n,
                NamespacePalette.ColorFor(n, overrides) ?? "",
                !overrides.ContainsKey(n),
                counts.GetValueOrDefault(n, 0)))
            .ToList();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        await LoadNamespacesAsync(ct);

        var aliases = await db.TagAliases.AsNoTracking()
            .Select(a => new
            {
                a.AliasTagId,
                AliasNamespace = a.AliasTag.Namespace,
                AliasName = a.AliasTag.Name,
                CanonicalNamespace = a.CanonicalTag.Namespace,
                CanonicalName = a.CanonicalTag.Name
            })
            .ToListAsync(ct);

        Aliases = aliases
            .Select(a => new AliasRow(
                a.AliasTagId,
                new TagName(a.AliasNamespace, a.AliasName).Display,
                new TagName(a.CanonicalNamespace, a.CanonicalName).Display))
            .OrderBy(a => a.Alias, StringComparer.Ordinal)
            .ToList();

        var implications = await db.TagImplications.AsNoTracking()
            .Select(i => new
            {
                i.ChildTagId,
                i.ParentTagId,
                ChildNamespace = i.ChildTag.Namespace,
                ChildName = i.ChildTag.Name,
                ParentNamespace = i.ParentTag.Namespace,
                ParentName = i.ParentTag.Name
            })
            .ToListAsync(ct);

        Implications = implications
            .Select(i => new ImplicationRow(
                i.ChildTagId,
                i.ParentTagId,
                new TagName(i.ChildNamespace, i.ChildName).Display,
                new TagName(i.ParentNamespace, i.ParentName).Display))
            .OrderBy(i => i.Child, StringComparer.Ordinal)
            .ToList();
    }
}
