namespace Boh.Web;

/// <summary>Gallery and detail URLs that carry the page and search the visitor came from.</summary>
public static class GalleryLinks
{
    /// <summary>Not <c>page</c>: Razor Pages reserves that route value.</summary>
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
