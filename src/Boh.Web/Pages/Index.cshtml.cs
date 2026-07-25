using Boh.Web.Data.Entities;
using Boh.Web.Services;
using Boh.Web.Tags;
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

    public async Task OnGetAsync(int page, string? q, CancellationToken ct)
    {
        CurrentPage = page < 1 ? 1 : page;
        Query = q;
        ViewData["Query"] = q;

        var resolved = await tags.ResolveSearchAsync(SearchQuery.Parse(q), ct);
        var (items, total) = await posts.ListAsync(resolved, CurrentPage, options.PageSize, ct);

        Posts = items;
        TotalCount = total;
        TotalPages = Math.Max(1, (int)Math.Ceiling(total / (double)options.PageSize));
    }

    /// <summary>Builds a gallery URL that carries the active search across pagination.</summary>
    public string PageUrl(int page)
    {
        var url = $"/?page={page}";
        return string.IsNullOrWhiteSpace(Query) ? url : $"{url}&q={Uri.EscapeDataString(Query)}";
    }
}
