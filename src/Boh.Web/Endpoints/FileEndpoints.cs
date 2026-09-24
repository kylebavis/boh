using System.Text.RegularExpressions;
using Boh.Web.Services;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace Boh.Web.Endpoints;

/// <summary>
/// Serves blobs out of the data directory, which lives outside wwwroot and so is not
/// reachable by the static file middleware.
/// </summary>
public static partial class FileEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>
    /// Blobs are immutable — their URL contains a hash of their bytes — so they can be
    /// cached indefinitely and revalidated with a strong ETag that costs nothing to compute.
    /// </summary>
    private const string ImmutableCacheControl = "public, max-age=31536000, immutable";

    public static void MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/files/o/{fileName}", (string fileName, HttpContext ctx, ContentAddressedFileStore store) =>
        {
            if (!TrySplit(fileName, out var sha, out var extension)) return Results.NotFound();

            return Serve(ctx, store.OriginalPath(sha, extension), sha, extension);
        }).WithName("OriginalFile");

        app.MapGet("/files/t/{fileName}", (string fileName, HttpContext ctx, ContentAddressedFileStore store) =>
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
        var match = BlobName().Match(fileName);
        sha = match.Groups[1].Value;
        extension = match.Groups[2].Value;
        return match.Success;
    }

    [GeneratedRegex(@"^([0-9a-f]{64})(\.[A-Za-z0-9]{1,7})\z")]
    private static partial Regex BlobName();
}
