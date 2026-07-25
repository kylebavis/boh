using Boh.Web.Services;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace Boh.Web.Endpoints;

/// <summary>
/// Serves blobs out of the data directory, which lives outside wwwroot and so is not
/// reachable by the static file middleware.
/// </summary>
public static class FileEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>
    /// Blobs are immutable — their URL contains a hash of their bytes — so they can be
    /// cached indefinitely and revalidated with a strong ETag that costs nothing to compute.
    /// </summary>
    private const string ImmutableCacheControl = "public, max-age=31536000, immutable";

    public static void MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/files/o/{fileName}", (string fileName, HttpContext ctx, IFileStore store) =>
        {
            if (!TrySplit(fileName, out var sha, out var extension)) return Results.NotFound();

            return Serve(ctx, store.OriginalPath(sha, extension), sha, extension);
        }).WithName("OriginalFile");

        app.MapGet("/files/t/{fileName}", (string fileName, HttpContext ctx, IFileStore store) =>
        {
            if (!TrySplit(fileName, out var sha, out var extension)) return Results.NotFound();
            if (extension != ".webp") return Results.NotFound();

            return Serve(ctx, store.ThumbPath(sha), sha, extension);
        }).WithName("ThumbnailFile");
    }

    private static IResult Serve(HttpContext ctx, string path, string sha, string extension)
    {
        if (!File.Exists(path)) return Results.NotFound();

        ctx.Response.Headers.CacheControl = ImmutableCacheControl;

        if (!ContentTypes.TryGetContentType(extension, out var contentType))
            contentType = "application/octet-stream";

        return Results.File(
            path,
            contentType,
            lastModified: null,
            entityTag: new EntityTagHeaderValue($"\"{sha}\""),
            enableRangeProcessing: true);   // Required for video seeking.
    }

    /// <summary>
    /// Splits "abc123….jpg" into hash and extension, rejecting anything that is not a
    /// plain lowercase hex digest and a short alphanumeric extension. This doubles as
    /// path traversal protection: no separators or dots can survive the check.
    /// </summary>
    private static bool TrySplit(string fileName, out string sha, out string extension)
    {
        sha = "";
        extension = "";

        var dot = fileName.LastIndexOf('.');
        if (dot <= 0 || dot == fileName.Length - 1) return false;

        var candidateSha = fileName[..dot];
        var candidateExt = fileName[dot..];

        if (candidateSha.Length != 64) return false;
        foreach (var c in candidateSha)
        {
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) return false;
        }

        if (candidateExt.Length is < 2 or > 8) return false;
        foreach (var c in candidateExt[1..])
        {
            if (!char.IsAsciiLetterOrDigit(c)) return false;
        }

        sha = candidateSha;
        extension = candidateExt;
        return true;
    }
}
