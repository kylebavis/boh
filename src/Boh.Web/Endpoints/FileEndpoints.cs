using System.Text.RegularExpressions;
using Boh.Web.Services;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace Boh.Web.Endpoints;

/// <summary>Serves blobs from the data directory, outside wwwroot.</summary>
public static partial class FileEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>URLs are content hashes, so cache forever.</summary>
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

    /// <summary>Splits "hash.ext", accepting only hex and a short extension, which also blocks path traversal.</summary>
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
