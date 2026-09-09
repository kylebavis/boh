namespace Boh.Web;

/// <summary>
/// What counts as an address a post can claim to have come from.
/// </summary>
/// <remarks>
/// One rule in one place because three callers need to agree on it: the import form, which
/// hands the URL to gallery-dl; the source editor on a post; and the sidecar reader, which
/// takes whatever an extractor happened to put in a field named like a page URL.
/// </remarks>
public static class SourceUrls
{
    public static bool IsAcceptable(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);

    /// <summary>The message shown wherever <see cref="IsAcceptable"/> turns something down.</summary>
    public const string Requirement = "Enter an absolute http:// or https:// URL.";
}
