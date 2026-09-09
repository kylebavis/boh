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
    /// <summary>
    /// Validates and rewrites a URL into the single form worth storing.
    /// </summary>
    /// <remarks>
    /// Canonicalizing rather than keeping what was typed matters because
    /// <see cref="Uri.TryCreate(string?, UriKind, out Uri?)"/> is far more permissive than it
    /// looks: verified on .NET 10, it accepts an absolute http URL with an embedded newline,
    /// carriage return, tab or NUL. Storing that raw put control characters into the database,
    /// into every log line quoting it, and into anything downstream reading the column —
    /// a newline lets a crafted address forge a whole log entry. <see cref="Uri.AbsoluteUri"/>
    /// percent-encodes all of them, so the value that leaves here is always a single line.
    /// <para>
    /// The same pass lowercases the scheme and host, which is why one post cannot end up with
    /// <c>https://Example.com</c> and <c>https://example.com/</c> as two separate sources.
    /// Path and query keep their case, since those are the halves a server may distinguish.
    /// </para>
    /// </remarks>
    public static bool TryCanonicalize(string? url, out string canonical)
    {
        canonical = "";

        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;

        canonical = parsed.AbsoluteUri;
        return true;
    }

    public static bool IsAcceptable(string? url) => TryCanonicalize(url, out _);

    /// <summary>The message shown wherever <see cref="IsAcceptable"/> turns something down.</summary>
    public const string Requirement = "Enter an absolute http:// or https:// URL.";
}
