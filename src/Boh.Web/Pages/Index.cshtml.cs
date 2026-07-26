using Boh.Web.Data.Entities;
using Boh.Web.Services;
using Boh.Web.Tags;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boh.Web.Pages;

public class IndexModel(PostService posts, TagService tags, BohOptions options) : PageModel
{
    public IReadOnlyList<Post> Posts { get; private set; } = [];
    public int CurrentPage { get; private set; } = 1;
    public int TotalPages { get; private set; } = 1;
    public int TotalCount { get; private set; }
    public string? Query { get; private set; }

    /// <summary>Gates the empty-state call to action, which is useless to a visitor who cannot upload.</summary>
    public bool CanUpload => options.AuthDisabled || User.Identity?.IsAuthenticated == true;

    /// <summary>
    /// <c>page</c> must be bound explicitly from the query string. Razor Pages reserves a
    /// route value of that exact name for the page's own path, and route values win over the
    /// query string — so a plain <c>int page</c> parameter received "/Index", failed to parse,
    /// and silently arrived as 0, pinning the gallery to page one however you navigated.
    /// </summary>
    public async Task OnGetAsync([FromQuery(Name = "page")] int page, string? q, CancellationToken ct)
    {
        Query = q;
        ViewData["Query"] = q;

        var resolved = await tags.ResolveSearchAsync(SearchQuery.Parse(q), ct);

        var requested = page < 1 ? 1 : page;
        var (items, total) = await posts.ListAsync(resolved, requested, options.PageSize, ct);
        TotalPages = Math.Max(1, (int)Math.Ceiling(total / (double)options.PageSize));

        // Clamp past the end rather than showing an empty gallery: deleting the last post on
        // the final page returns here with a page number that no longer exists.
        if (requested > TotalPages)
        {
            requested = TotalPages;
            (items, total) = await posts.ListAsync(resolved, requested, options.PageSize, ct);
        }

        CurrentPage = requested;
        Posts = items;
        TotalCount = total;
    }

    /// <summary>Builds a gallery URL that carries the active search across pagination.</summary>
    public string PageUrl(int page) => GalleryLinks.Gallery(page, Query);
}
