namespace Boh.Web;

/// <summary>
/// Builds gallery and post-detail URLs that carry the browsing context — which page of which
/// search the visitor came from — so that navigating into a post and back, or deleting one,
/// lands where they left off.
/// </summary>
public static class GalleryLinks
{
    /// <summary>
    /// The return page is <em>not</em> named <c>page</c>. Razor Pages reserves a route value
    /// of that name for the page's own path on every page, and route values beat the query
    /// string, so a <c>page</c> parameter on the detail page would never bind.
    /// </summary>
    public const string FromPageKey = "fromPage";

    /// <summary>A gallery URL for the given page and search. Page 1 is left implicit.</summary>
    public static string Gallery(int page, string? query)
    {
        var url = page <= 1 ? "/" : $"/?page={page}";
        return Append(url, "q", query);
    }

    /// <summary>A post URL that remembers the listing it was opened from.</summary>
    public static string Detail(int postId, int fromPage, string? query)
    {
        var url = $"/Posts/Detail/{postId}";
        if (fromPage > 1) url = Append(url, FromPageKey, fromPage.ToString());
        return Append(url, "q", query);
    }

    private static string Append(string url, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return url;

        var separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}{key}={Uri.EscapeDataString(value)}";
    }
}
