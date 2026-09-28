namespace Boh.Web;

/// <summary>The one rule for what counts as a post source URL.</summary>
public static class SourceUrls
{
    /// <summary>
    /// Validates and canonicalizes an http(s) URL. <see cref="Uri.AbsoluteUri"/> percent-encodes
    /// control characters that <see cref="Uri.TryCreate(string?, UriKind, out Uri?)"/> lets through,
    /// and lowercases scheme and host.
    /// </summary>
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
