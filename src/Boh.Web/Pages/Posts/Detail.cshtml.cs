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
    /// <summary>
    /// How many look-alikes the page will show. A handful is enough to judge whether the post
    /// is a repost; <c>similar:</c> in the search box lists them all in the gallery.
    /// </summary>
    private const int MaxSimilarShown = 8;

    public Post Post { get; private set; } = null!;
    public PostTagView TagView { get; private set; } = null!;
    public PostSourceView SourceView { get; private set; } = null!;

    /// <summary>
    /// Posts that look like this one, closest first. Recomputed per view rather than stored:
    /// what looks like this post changes as the collection grows, and a cached answer would
    /// go quietly stale.
    /// </summary>
    public IReadOnlyList<SimilarPostCard> Similar { get; private set; } = [];

    /// <summary>The gallery search that lists every look-alike rather than the first few.</summary>
    public string SimilarSearchUrl =>
        GalleryLinks.Gallery(1, $"{SearchQuery.SimilarPrefix}{Post.Id}");

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

    /// <summary>
    /// Takes the namespace and name as separate parameters rather than one
    /// <c>namespace:name</c> string, because the two are not recoverable from the joined
    /// form. A tag with no namespace whose name contains a colon — <c>artist:orb_enjoyer</c>,
    /// the shape an import produces when the site packs its own category into the tag text —
    /// parses back as namespace <c>artist</c>, naming a tag the post does not carry. The
    /// removal then rewrote the post's tags without dropping anything and returned the
    /// unchanged list, so the chip stayed put with no error to explain it.
    /// </summary>
    public async Task<IActionResult> OnPostRemoveTagAsync(int id, string? ns, string? name, CancellationToken ct)
    {
        if (TagName.TryParseInNamespace(ns, name, out var parsed)) await tags.RemovePostTagAsync(id, parsed, ct);

        return await TagFragmentAsync(id, null, ct);
    }

    /// <summary>
    /// Adding a source by hand, which is the only way a direct upload gets one — an import
    /// records where it fetched from, but nothing knows where a file dragged in came from.
    /// </summary>
    public async Task<IActionResult> OnPostAddSourceAsync(int id, CancellationToken ct)
    {
        var url = Request.Form["url"].ToString().Trim();

        if (!SourceUrls.IsAcceptable(url))
        {
            return await SourceFragmentAsync(id,
                url.Length == 0 ? null : SourceUrls.Requirement, ct);
        }

        // A URL the post already carries is not an error worth reporting: the list the visitor
        // is looking at already says so, and it comes back re-rendered either way.
        await posts.AddSourceAsync(id, url, ct);
        return await SourceFragmentAsync(id, null, ct);
    }

    public async Task<IActionResult> OnPostRemoveSourceAsync(int id, int sourceId, CancellationToken ct)
    {
        await posts.RemoveSourceAsync(id, sourceId, ct);
        return await SourceFragmentAsync(id, null, ct);
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

    /// <summary>Re-renders just the source block, which is what HTMX swaps in.</summary>
    private async Task<IActionResult> SourceFragmentAsync(int postId, string? error, CancellationToken ct)
    {
        var post = await posts.GetAsync(postId, ct);
        if (post is null) return NotFound();

        return Partial("_SourceList", BuildSourceView(post) with { Error = error });
    }

    /// <summary>
    /// Sorted here rather than relying on the query's ordering. Re-reading the post in the
    /// same request that just added a source finds that row already tracked, and EF fixes it
    /// into the collection ahead of the rows the query returned — so the freshly added source
    /// jumped to the top of the swapped-in fragment and settled back on the next page load.
    /// </summary>
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
