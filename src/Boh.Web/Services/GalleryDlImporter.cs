using System.Text.Json;
using Boh.Web.Jobs;
using Boh.Web.Tags;

namespace Boh.Web.Services;

/// <summary>A stored file. <paramref name="Similar"/>: existing look-alikes, for a person to judge.</summary>
public sealed record ImportedItem(
    int PostId,
    string Sha256,
    string FileName,
    IReadOnlyList<string> Tags,
    IReadOnlyList<SimilarPost> Similar);

/// <summary>A skipped file. <paramref name="DuplicateOfPostId"/> is set when it's already a post.</summary>
public sealed record SkippedItem(string FileName, string Reason, int? DuplicateOfPostId = null);

public sealed record ImportResult(
    IReadOnlyList<ImportedItem> Created,
    IReadOnlyList<SkippedItem> Skipped,
    string? Error)
{
    public bool Failed => Error is not null;
}

/// <summary>
/// Imports from a URL via gallery-dl. Fetches a user-chosen URL, so always authenticated;
/// capped by <c>--range</c> and a timeout since imports share one lane.
/// </summary>
public sealed class GalleryDlImporter(
    ProcessRunner runner,
    PostService posts,
    TagService tags,
    BohOptions options,
    ILogger<GalleryDlImporter> logger)
{
    /// <summary>The <see cref="JobSnapshot.Kind"/> an import is queued under.</summary>
    public const string JobKind = "import";

    /// <summary>Queues an import of an already canonicalized URL.</summary>
    public static JobSnapshot Enqueue(JobQueue jobs, string url, int? userId) =>
        jobs.Enqueue(JobLane.Import, JobKind, url, userId, async job =>
            await job.Services.GetRequiredService<GalleryDlImporter>()
                .ImportAsync(url, userId, job, job.CancellationToken));

    private static readonly HashSet<string> MetadataExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json"
    };

    public async Task<ImportResult> ImportAsync(
        string url, int? uploadedById, IProgress<JobProgress>? progress, CancellationToken ct)
    {
        // Canonical from here on; the raw input may carry control characters.
        if (!SourceUrls.TryCanonicalize(url, out var galleryUrl))
        {
            return new ImportResult([], [], SourceUrls.Requirement);
        }

        Directory.CreateDirectory(options.ImportTempDir);
        var workingDirectory = Path.Combine(options.ImportTempDir, $"import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);

        try
        {
            progress?.Report(new JobProgress("Downloading with gallery-dl"));

            var result = await runner.RunAsync("gallery-dl", BuildArguments(galleryUrl, workingDirectory),
                TimeSpan.FromSeconds(options.ImportTimeoutSec), ct);

            if (result.TimedOut)
            {
                return new ImportResult([], [],
                    $"gallery-dl did not finish within {options.ImportTimeoutSec}s and was stopped.");
            }

            var mediaFiles = Directory
                .EnumerateFiles(workingDirectory, "*", SearchOption.AllDirectories)
                .Where(path => !MetadataExtensions.Contains(Path.GetExtension(path)))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            if (mediaFiles.Count == 0)
            {
                // Non-zero exit and no files: surface what it said.
                var detail = FirstMeaningfulLine(result.StandardError) ?? FirstMeaningfulLine(result.StandardOutput);
                return new ImportResult([], [],
                    detail is null
                        ? "gallery-dl downloaded nothing from that URL."
                        : $"gallery-dl downloaded nothing from that URL: {detail}");
            }

            return await IngestAsync(mediaFiles, galleryUrl, uploadedById, progress, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Import of {Url} failed", galleryUrl);
            return new ImportResult([], [], "The import failed. The server log has the details.");
        }
        finally
        {
            TryCleanup(workingDirectory);
        }
    }

    private List<string> BuildArguments(string url, string workingDirectory)
    {
        var arguments = new List<string>
        {
            "--write-metadata",           // per-file {name}.json sidecars
            "--directory", workingDirectory,
            "--range", $"1-{options.ImportMax}",
            "--retries", "2",
        };

        // Lets the operator supply site credentials without rebuilding the image.
        var configPath = options.GalleryDlConfigPath;
        if (File.Exists(configPath))
        {
            arguments.Add("--config");
            arguments.Add(configPath);
        }

        arguments.Add(url);
        return arguments;
    }

    private async Task<ImportResult> IngestAsync(
        List<string> mediaFiles,
        string galleryUrl,
        int? uploadedById,
        IProgress<JobProgress>? progress,
        CancellationToken ct)
    {
        var created = new List<ImportedItem>();
        var skipped = new List<SkippedItem>();

        for (var i = 0; i < mediaFiles.Count; i++)
        {
            // Between files, so cancelling keeps stored posts whole.
            ct.ThrowIfCancellationRequested();
            progress?.Report(new JobProgress("Storing files", i, mediaFiles.Count));

            var path = mediaFiles[i];
            var fileName = Path.GetFileName(path);
            var metadata = ReadSidecar(path);

            // Prefer the file's own page; the typed URL is a fallback.
            var source = GalleryDlSourceMapper.PageUrl(metadata) ?? galleryUrl;

            await using var stream = File.OpenRead(path);
            var result = await posts.CreateAsync(stream, uploadedById, source, ct);

            switch (result)
            {
                case PostCreateResult.Created createdPost:
                    var mapped = GalleryDlTagMapper.Map(metadata);
                    var stored = new List<string>();

                    if (mapped.Count > 0)
                    {
                        await tags.AddPostTagsAsync(createdPost.Post.Id, mapped, ct);

                        // Read back, so aliased names show as stored.
                        stored = (await tags.GetExplicitTagNamesAsync(createdPost.Post.Id, ct))
                            .Select(t => t.Display)
                            .OrderBy(t => t, StringComparer.Ordinal)
                            .ToList();
                    }

                    created.Add(new ImportedItem(
                        createdPost.Post.Id,
                        createdPost.Post.Sha256,
                        fileName,
                        stored,
                        createdPost.Similar));
                    break;

                // Still records this URL on the existing post.
                case PostCreateResult.Duplicate duplicate:
                    skipped.Add(new SkippedItem(
                        fileName,
                        duplicate.SourceAdded
                            ? "added this URL as another source; already stored as"
                            : "already stored as",
                        duplicate.ExistingPostId));
                    break;

                case PostCreateResult.Rejected rejected:
                    skipped.Add(new SkippedItem(fileName, rejected.Reason));
                    break;
            }
        }

        logger.LogInformation("Imported {Created} file(s) from {Url}, skipped {Skipped}",
            created.Count, galleryUrl, skipped.Count);

        return new ImportResult(created, skipped, null);
    }

    /// <summary>Reads gallery-dl's <c>{filename}.json</c> sidecar; missing is normal.</summary>
    private JsonElement? ReadSidecar(string mediaPath)
    {
        var sidecar = mediaPath + ".json";
        if (!File.Exists(sidecar)) return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(sidecar));
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Could not parse sidecar {Path}", sidecar);
            return null;
        }
    }

    private static string? FirstMeaningfulLine(string output)
    {
        var line = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.Length > 0);

        return line is null ? null : line.Length > 300 ? line[..300] : line;
    }

    private void TryCleanup(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not remove import scratch directory {Directory}", directory);
        }
    }
}
