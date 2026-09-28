using Boh.Web.Data.Entities;
using Boh.Web.Services;
using Boh.Web.Tags;
using Boh.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boh.Web.Pages.Posts;

public class DetailModel(
    PostService posts,
    TagService tags,
    DuplicateService duplicates,
    BohOptions options) : PageModel
{
    private const int MaxSimilarShown = 8;

    public Post Post { get; private set; } = null!;
    public PostTagView TagView { get; private set; } = null!;
    public PostSourceView SourceView { get; private set; } = null!;

    /// <summary>Look-alikes, closest first. Computed per view so it never goes stale.</summary>
    public IReadOnlyList<SimilarPostCard> Similar { get; private set; } = [];

    /// <summary>The gallery search that lists every look-alike rather than the first few.</summary>
    public string SimilarSearchUrl =>
        GalleryLinks.Gallery(1, $"{SearchQuery.SimilarPrefix}{Post.Id}");

    /// <summary>True when the current visitor may modify this post.</summary>
    public bool CanEdit => options.AuthDisabled || User.Identity?.IsAuthenticated == true;

    /// <summary>Echoed into the delete form.</summary>
    public string? Query { get; private set; }

    /// <summary>The gallery page the visitor arrived from.</summary>
    public int FromPage { get; private set; } = 1;

    public async Task<IActionResult> OnGetAsync(int id, string? q, int fromPage, CancellationToken ct)
    {
        var post = await posts.GetAsync(id, ct);
        if (post is null) return NotFound();

        Query = q;
        FromPage = fromPage < 1 ? 1 : fromPage;

        // Keeps the search box filled and Random scoped.
        ViewData["Query"] = q;

        Post = post;
        TagView = BuildTagView(post, await tags.GetNamespaceColorsAsync(ct));
        SourceView = BuildSourceView(post);
        Similar = await duplicates.GetSimilarToPostAsync(id, MaxSimilarShown, ct);
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

    /// <summary>Namespace and name arrive separately: a joined string can't be split back when the name has a colon.</summary>
    public async Task<IActionResult> OnPostRemoveTagAsync(int id, string? ns, string? name, CancellationToken ct)
    {
        if (TagName.TryParseInNamespace(ns, name, out var parsed)) await tags.RemovePostTagAsync(id, parsed, ct);

        return await TagFragmentAsync(id, null, ct);
    }

    public async Task<IActionResult> OnPostAddSourceAsync(int id, CancellationToken ct)
    {
        var url = Request.Form["url"].ToString().Trim();

        if (!SourceUrls.IsAcceptable(url))
        {
            return await SourceFragmentAsync(id,
                url.Length == 0 ? null : SourceUrls.Requirement, ct);
        }

        // An already-present URL is not an error.
        await posts.AddSourceAsync(id, url, ct);
        return await SourceFragmentAsync(id, null, ct);
    }

    public async Task<IActionResult> OnPostRemoveSourceAsync(int id, int sourceId, CancellationToken ct)
    {
        await posts.RemoveSourceAsync(id, sourceId, ct);
        return await SourceFragmentAsync(id, null, ct);
    }

    /// <summary>Returns to the listing the visitor deleted from; the gallery clamps the page.</summary>
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

    /// <summary>Re-renders just the source block, which is what HTMX swaps in.</summary>
    private async Task<IActionResult> SourceFragmentAsync(int postId, string? error, CancellationToken ct)
    {
        var post = await posts.GetAsync(postId, ct);
        if (post is null) return NotFound();

        return Partial("_SourceList", BuildSourceView(post) with { Error = error });
    }

    /// <summary>Sorted here: a just-added source is already tracked and EF would put it first.</summary>
    private PostSourceView BuildSourceView(Post post) => new(
        post.Id,
        [.. post.Sources.OrderBy(s => s.Id).Select(s => new PostSourceEntry(s.Id, s.Url))],
        CanEdit);

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
            // Explicit first, then by namespace so same-coloured tags sit together.
            .OrderBy(e => e.Implied)
            .ThenBy(e => e.Namespace.Length == 0)
            .ThenBy(e => e.Namespace, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();

        return new PostTagView(post.Id, entries, CanEdit);
    }
}
