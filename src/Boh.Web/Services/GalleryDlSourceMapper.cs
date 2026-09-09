using System.Text.Json;

namespace Boh.Web.Services;

/// <summary>
/// Finds the page a downloaded file came from in its gallery-dl metadata sidecar.
/// </summary>
/// <remarks>
/// Importing a gallery gives every file the same typed-in URL, which says where the import
/// started rather than where each file lives. Extractors that know the per-file page put it
/// in the sidecar, so prefer that when it is there.
/// <para>
/// The field names are the ones gallery-dl 1.32 actually populates with a page address,
/// checked against the pinned version rather than guessed: <c>post_url</c> (instagram,
/// newgrounds, imagehosts and a handful of smaller extractors), <c>page_url</c>, and
/// <c>webpage_url</c> for anything routed through ytdl. Deliberately absent is the far more
/// common <c>url</c>, which nearly always holds the media file itself — a direct CDN link that
/// often expires, and a worse record than the gallery it came from. Booru and social-media
/// extractors mostly publish no page field at all; those imports keep falling back.
/// </para>
/// </remarks>
public static class GalleryDlSourceMapper
{
    /// <summary>In priority order; the first field carrying a usable address wins.</summary>
    private static readonly string[] PageUrlFields = ["post_url", "page_url", "webpage_url"];

    /// <summary>
    /// Null when the sidecar names no page, which is the ordinary case — the caller then has
    /// nothing better than the URL the import was started from.
    /// </summary>
    public static string? PageUrl(JsonElement? metadata)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object } root) return null;

        foreach (var field in PageUrlFields)
        {
            if (!root.TryGetProperty(field, out var value)) continue;
            if (value.ValueKind != JsonValueKind.String) continue;

            // A relative value is not usable and not worth repairing: reddit's `permalink` is
            // a path, and guessing the host it belongs to is how you record a wrong address.
            var url = value.GetString()?.Trim();
            if (SourceUrls.IsAcceptable(url)) return url;
        }

        return null;
    }
}
