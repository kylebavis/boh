using Boh.Web.Services;
using Boh.Web.Tags;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boh.Web.Pages.Posts;

/// <summary>
/// Redirects to a randomly chosen post. Carries the active search through, so "random" from a
/// filtered gallery stays inside that filter rather than jumping to the whole collection.
/// </summary>
public class RandomModel(PostService posts, TagService tags) : PageModel
{
    public async Task<IActionResult> OnGetAsync(string? q, CancellationToken ct)
    {
        var resolved = await tags.ResolveSearchAsync(SearchQuery.Parse(q), ct);
        var id = await posts.GetRandomIdAsync(resolved, ct);

        if (id is null)
        {
            // Nothing to jump to — send them back to the gallery, keeping the search so the
            // empty-state message explains why.
            return Redirect(GalleryLinks.Gallery(1, q));
        }

        return Redirect(GalleryLinks.Detail(id.Value, 1, q));
    }
}
