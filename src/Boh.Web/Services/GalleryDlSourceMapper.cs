using System.Text.Json;

namespace Boh.Web.Services;

/// <summary>
/// The per-file page URL from a gallery-dl sidecar, when the extractor reports one. Not
/// <c>url</c>, which is usually the media file itself.
/// </summary>
public static class GalleryDlSourceMapper
{
    /// <summary>Priority order.</summary>
    private static readonly string[] PageUrlFields = ["post_url", "page_url", "webpage_url"];

    /// <summary>Null when the sidecar names no page.</summary>
    public static string? PageUrl(JsonElement? metadata)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object } root) return null;

        foreach (var field in PageUrlFields)
        {
            if (!root.TryGetProperty(field, out var value)) continue;
            if (value.ValueKind != JsonValueKind.String) continue;

            // Relative values (e.g. reddit permalinks) aren't usable.
            var url = value.GetString()?.Trim();
            if (SourceUrls.IsAcceptable(url)) return url;
        }

        return null;
    }
}
