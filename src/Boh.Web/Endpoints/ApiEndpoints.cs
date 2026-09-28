using Boh.Web.Data.Entities;
using Boh.Web.Jobs;
using Boh.Web.Security;
using Boh.Web.Services;
using Boh.Web.Tags;
using Microsoft.AspNetCore.Mvc;

namespace Boh.Web.Endpoints;

public sealed record ApiPostSummary(int Id, int Width, int Height, bool IsVideo, string ThumbUrl);

public sealed record ApiPostPage(int Page, int TotalPages, int TotalCount, IReadOnlyList<ApiPostSummary> Posts);

public sealed record ApiTag(string Namespace, string Name, string Display, bool Implied);

public sealed record ApiSource(int Id, string Url);

public sealed record ApiPost(
    int Id,
    string Sha256,
    string MimeType,
    long FileSizeBytes,
    int Width,
    int Height,
    double? DurationSec,
    bool IsVideo,
    DateTimeOffset UploadedAt,
    string? UploadedBy,
    string PageUrl,
    string FileUrl,
    string ThumbUrl,
    IReadOnlyList<ApiTag> Tags,
    IReadOnlyList<ApiSource> Sources);

public sealed record ApiImport(
    Guid Id,
    string Url,
    JobState State,
    JobProgress? Progress,
    string? Message,
    ImportResult? Result,
    DateTimeOffset QueuedAt,
    DateTimeOffset? FinishedAt);

public sealed record ApiTagsBody(string[]? Tags);

public sealed record ApiUrlBody(string? Url);

/// <summary>JSON API for scripts. Token auth only, so no antiforgery.</summary>
public static class ApiEndpoints
{
    private const int MaxAutocomplete = 50;

    public static void MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").DisableAntiforgery();
        var read = api.MapGroup("").RequireAuthorization(BohPolicies.ApiRead);
        var write = api.MapGroup("").RequireAuthorization(BohPolicies.ApiWrite);

        read.MapGet("/posts", async (
            string? q, int? page, PostService posts, TagService tags, BohOptions options, CancellationToken ct) =>
        {
            var search = await tags.ResolveSearchAsync(SearchQuery.Parse(q), ct);
            var requested = Math.Max(1, page ?? 1);
            var (items, total) = await posts.ListAsync(search, requested, options.PageSize, ct);

            return new ApiPostPage(
                requested,
                Math.Max(1, (int)Math.Ceiling(total / (double)options.PageSize)),
                total,
                [.. items.Select(p => new ApiPostSummary(p.Id, p.Width, p.Height, p.IsVideo, PostUrls.Thumb(p.Sha256)))]);
        });

        read.MapGet("/posts/random", async (string? q, PostService posts, TagService tags, CancellationToken ct) =>
        {
            var search = await tags.ResolveSearchAsync(SearchQuery.Parse(q), ct);
            return await posts.GetRandomIdAsync(search, ct) is { } id
                ? Results.Ok(new { id })
                : Results.NotFound();
        });

        read.MapGet("/posts/{id:int}", async (int id, PostService posts, CancellationToken ct) =>
            await PostAsync(posts, id, ct));

        read.MapGet("/tags", async (string? q, int? limit, TagService tags, CancellationToken ct) =>
            await tags.AutocompleteAsync(q, Math.Clamp(limit ?? 10, 1, MaxAutocomplete), ct));

        write.MapPost("/posts", async (
            IFormFile? file,
            [FromForm] string? tags,
            [FromForm] string? source,
            HttpContext http,
            PostService posts,
            TagService tagService,
            CancellationToken ct) =>
        {
            if (file is null || file.Length == 0) return Error("Send the file as a multipart field named \"file\".");
            if (!string.IsNullOrWhiteSpace(source) && !SourceUrls.IsAcceptable(source)) return Error(SourceUrls.Requirement);
            if (!TryParseTags(tags, out var parsed)) return Error("Nothing in that input is a usable tag.");

            await using var stream = file.OpenReadStream();
            var result = await posts.CreateAsync(stream, UserPrincipal.GetId(http.User), source ?? "", ct);

            switch (result)
            {
                case PostCreateResult.Created created:
                    if (parsed.Count > 0) await tagService.AddPostTagsAsync(created.Post.Id, parsed, ct);
                    return Results.Created($"/api/v1/posts/{created.Post.Id}", ToApi((await posts.GetAsync(created.Post.Id, ct))!));

                case PostCreateResult.Duplicate duplicate:
                    return Results.Conflict(new { error = "That file is already a post.", postId = duplicate.ExistingPostId });

                case PostCreateResult.Rejected rejected:
                    return Error(rejected.Reason);

                default:
                    throw new InvalidOperationException($"Unhandled result {result.GetType().Name}");
            }
        });

        write.MapDelete("/posts/{id:int}", async (int id, PostService posts, CancellationToken ct) =>
            await posts.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        write.MapPost("/posts/{id:int}/tags", (int id, ApiTagsBody body, PostService posts, TagService tags, CancellationToken ct) =>
            UpdateTagsAsync(id, body, posts, ct, parsed => tags.AddPostTagsAsync(id, parsed, ct)));

        write.MapPut("/posts/{id:int}/tags", (int id, ApiTagsBody body, PostService posts, TagService tags, CancellationToken ct) =>
            UpdateTagsAsync(id, body, posts, ct, parsed => tags.SetPostTagsAsync(id, parsed, ct)));

        write.MapPost("/posts/{id:int}/sources", async (int id, ApiUrlBody body, PostService posts, CancellationToken ct) =>
        {
            if (!SourceUrls.IsAcceptable(body.Url)) return Error(SourceUrls.Requirement);
            if (await posts.GetAsync(id, ct) is null) return Results.NotFound();

            await posts.AddSourceAsync(id, body.Url, ct);
            return await PostAsync(posts, id, ct);
        });

        write.MapDelete("/posts/{id:int}/sources/{sourceId:int}", async (int id, int sourceId, PostService posts, CancellationToken ct) =>
            await posts.RemoveSourceAsync(id, sourceId, ct) ? Results.NoContent() : Results.NotFound());

        write.MapPost("/imports", (ApiUrlBody body, HttpContext http, JobQueue jobs) =>
        {
            if (!SourceUrls.TryCanonicalize(body.Url, out var url)) return Error(SourceUrls.Requirement);

            var job = GalleryDlImporter.Enqueue(jobs, url, UserPrincipal.GetId(http.User));
            return Results.Accepted($"/api/v1/imports/{job.Id}", ToApi(job));
        });

        // Someone else's import is treated as absent.
        write.MapGet("/imports/{id:guid}", (Guid id, HttpContext http, JobQueue jobs) =>
            jobs.Get(id) is { Kind: GalleryDlImporter.JobKind } job && job.RequestedById == UserPrincipal.GetId(http.User)
                ? Results.Ok(ToApi(job))
                : Results.NotFound());
    }

    private static async Task<IResult> UpdateTagsAsync(
        int id, ApiTagsBody body, PostService posts, CancellationToken ct, Func<List<TagName>, Task> apply)
    {
        if (!TryParseTags(string.Join(' ', body.Tags ?? []), out var parsed)) return Error("Nothing in that input is a usable tag.");
        if (await posts.GetAsync(id, ct) is null) return Results.NotFound();

        await apply(parsed);
        return await PostAsync(posts, id, ct);
    }

    /// <summary>False only when there was input and none of it parsed.</summary>
    private static bool TryParseTags(string? input, out List<TagName> parsed)
    {
        parsed = TagName.ParseMany(input);
        return parsed.Count > 0 || string.IsNullOrWhiteSpace(input);
    }

    private static async Task<IResult> PostAsync(PostService posts, int id, CancellationToken ct) =>
        await posts.GetAsync(id, ct) is { } post ? Results.Ok(ToApi(post)) : Results.NotFound();

    private static IResult Error(string reason) => Results.BadRequest(new { error = reason });

    private static ApiPost ToApi(Post post) => new(
        post.Id,
        post.Sha256,
        post.MimeType,
        post.FileSizeBytes,
        post.Width,
        post.Height,
        post.DurationSec,
        post.IsVideo,
        post.UploadedAt,
        post.UploadedBy?.Username,
        $"/Posts/Detail/{post.Id}",
        PostUrls.Original(post),
        PostUrls.Thumb(post),
        [
            .. post.PostTags
                .OrderBy(pt => pt.Source)
                .ThenBy(pt => pt.Tag.Namespace, StringComparer.Ordinal)
                .ThenBy(pt => pt.Tag.Name, StringComparer.Ordinal)
                .Select(pt => new ApiTag(
                    pt.Tag.Namespace,
                    pt.Tag.Name,
                    new TagName(pt.Tag.Namespace, pt.Tag.Name).Display,
                    pt.Source == TagSource.Implied))
        ],
        [.. post.Sources.OrderBy(s => s.Id).Select(s => new ApiSource(s.Id, s.Url))]);

    private static ApiImport ToApi(JobSnapshot job) => new(
        job.Id, job.Title, job.State, job.Progress, job.Message, job.Result as ImportResult, job.QueuedAt, job.FinishedAt);
}
