using Boh.Web.Data.Entities;
using Boh.Web.Services;
using Boh.Web.Tags;
using Boh.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boh.Web.Pages.Posts;

public class DetailModel(PostService posts, TagService tags, BohOptions options) : PageModel
{
    public Post Post { get; private set; } = null!;
    public PostTagView TagView { get; private set; } = null!;

    /// <summary>True when the current visitor may modify this post.</summary>
    public bool CanEdit => options.AuthDisabled || User.Identity?.IsAuthenticated == true;

    /// <summary>The search the visitor arrived with, echoed back into the delete form.</summary>
    public string? Query { get; private set; }

    /// <summary>The gallery page the visitor arrived from.</summary>
    public int FromPage { get; private set; } = 1;

    public async Task<IActionResult> OnGetAsync(int id, string? q, int fromPage, CancellationToken ct)
    {
        var post = await posts.GetAsync(id, ct);
        if (post is null) return NotFound();

        Query = q;
        FromPage = fromPage < 1 ? 1 : fromPage;

        // Keeps the header search box filled and Random scoped to the same search.
        ViewData["Query"] = q;

        Post = post;
        TagView = BuildTagView(post, await tags.GetNamespaceColorsAsync(ct));
        return Page();
    }

    public async Task<IActionResult> OnPostAddTagsAsync(int id, CancellationToken ct)
    {
        var input = Request.Form["tags"].ToString();
        var parsed = TagName.ParseMany(input);

        if (parsed.Count == 0)
        {
            return await TagFragmentAsync(id,
                string.IsNullOrWhiteSpace(input) ? null : "Nothing in that input is a usable tag.", ct);
        }

        await tags.AddPostTagsAsync(id, parsed, ct);
        return await TagFragmentAsync(id, null, ct);
    }

    public async Task<IActionResult> OnPostRemoveTagAsync(int id, string? tag, CancellationToken ct)
    {
        if (TagName.TryParse(tag, out var parsed)) await tags.RemovePostTagAsync(id, parsed, ct);

        return await TagFragmentAsync(id, null, ct);
    }

    /// <summary>
    /// <paramref name="q"/> and <paramref name="fromPage"/> come from hidden fields on the
    /// delete form, so the visitor lands back in the listing they deleted from. The gallery
    /// clamps an overshooting page, which covers deleting the last post on the final page.
    /// </summary>
    public async Task<IActionResult> OnPostDeleteAsync(int id, string? q, int fromPage, CancellationToken ct)
    {
        await posts.DeleteAsync(id, ct);
        return Redirect(GalleryLinks.Gallery(fromPage < 1 ? 1 : fromPage, q));
    }

    /// <summary>Re-renders just the tag block, which is what HTMX swaps in.</summary>
    private async Task<IActionResult> TagFragmentAsync(int postId, string? error, CancellationToken ct)
    {
        var post = await posts.GetAsync(postId, ct);
        if (post is null) return NotFound();

        var colors = await tags.GetNamespaceColorsAsync(ct);
        return Partial("_TagList", BuildTagView(post, colors) with { Error = error });
    }

    private PostTagView BuildTagView(Post post, IReadOnlyDictionary<string, string> namespaceColors)
    {
        var entries = post.PostTags
            .Select(pt => new PostTagEntry(
                new TagName(pt.Tag.Namespace, pt.Tag.Name).Display,
                pt.Tag.Name,
                pt.Tag.Namespace,
                pt.Source == TagSource.Implied,
                pt.Tag.PostCount,
                NamespacePalette.ColorFor(pt.Tag.Namespace, namespaceColors)))
            // Explicit first, then grouped by namespace so same-coloured tags sit together —
            // which is what makes the colour legible now that the prefix is not printed.
            .OrderBy(e => e.Implied)
            .ThenBy(e => e.Namespace.Length == 0)
            .ThenBy(e => e.Namespace, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();

        return new PostTagView(post.Id, entries, CanEdit);
    }
}
