using System.Text.Json;
using Boh.Web.Tags;

namespace Boh.Web.Services;

public sealed record ImportedItem(int PostId, string Sha256, string FileName, IReadOnlyList<string> Tags);
public sealed record SkippedItem(string FileName, string Reason);

public sealed record ImportResult(
    IReadOnlyList<ImportedItem> Created,
    IReadOnlyList<SkippedItem> Skipped,
    string? Error)
{
    public bool Failed => Error is not null;
}

/// <summary>
/// Imports posts from a third-party URL by driving the bundled gallery-dl binary.
/// </summary>
/// <remarks>
/// This fetches a URL chosen by the user from inside the container, so it is gated behind
/// authentication regardless of BOH_PUBLIC_READ. The run is bounded on both axes —
/// <c>--range</c> caps how many files a single gallery can produce, and the process is
/// killed after a timeout — because the whole thing happens inside one HTTP request.
/// </remarks>
public sealed class GalleryDlImporter(
    ProcessRunner runner,
    PostService posts,
    TagService tags,
    BohOptions options,
    ILogger<GalleryDlImporter> logger)
{
    private static readonly HashSet<string> MetadataExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json"
    };

    public async Task<ImportResult> ImportAsync(string url, int? uploadedById, CancellationToken ct)
    {
        // Canonical from here on: what gets fetched, logged and recorded is the rewritten form,
        // never the raw submission. Uri.TryCreate alone would let control characters through
        // into the log — see SourceUrls.TryCanonicalize.
        if (!SourceUrls.TryCanonicalize(url, out var galleryUrl))
        {
            return new ImportResult([], [], SourceUrls.Requirement);
        }

        Directory.CreateDirectory(options.ImportTempDir);
        var workingDirectory = Path.Combine(options.ImportTempDir, $"import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);

        try
        {
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
                // A non-zero exit with no files is the informative case; surface what it said.
                var detail = FirstMeaningfulLine(result.StandardError) ?? FirstMeaningfulLine(result.StandardOutput);
                return new ImportResult([], [],
                    detail is null
                        ? "gallery-dl downloaded nothing from that URL."
                        : $"gallery-dl downloaded nothing from that URL: {detail}");
            }

            return await IngestAsync(mediaFiles, galleryUrl, uploadedById, ct);
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
        List<string> mediaFiles, string galleryUrl, int? uploadedById, CancellationToken ct)
    {
        var created = new List<ImportedItem>();
        var skipped = new List<SkippedItem>();

        foreach (var path in mediaFiles)
        {
            var fileName = Path.GetFileName(path);
            var metadata = ReadSidecar(path);

            // The page this particular file lives on, when the extractor reports one. The
            // typed URL is only a fallback: importing an artist's gallery would otherwise
            // stamp all forty posts with the same address, which points at none of them.
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

                        // Read the tags back rather than reporting what the mapper produced:
                        // an aliased name is stored as its canonical form, and the summary
                        // showing the alias makes it look as though the alias was ignored.
                        stored = (await tags.GetExplicitTagNamesAsync(createdPost.Post.Id, ct))
                            .Select(t => t.Display)
                            .OrderBy(t => t, StringComparer.Ordinal)
                            .ToList();
                    }

                    created.Add(new ImportedItem(
                        createdPost.Post.Id,
                        createdPost.Post.Sha256,
                        fileName,
                        stored));
                    break;

                // Skipped as a post, but not as information: the file being reachable from
                // this URL too is recorded on the post that already holds it.
                case PostCreateResult.Duplicate duplicate:
                    skipped.Add(new SkippedItem(fileName, duplicate.SourceAdded
                        ? $"already stored as post {duplicate.ExistingPostId}; added this URL as another source"
                        : $"already stored as post {duplicate.ExistingPostId}"));
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

    /// <summary>
    /// gallery-dl writes metadata beside each file as <c>{filename}.json</c>.
    /// A missing sidecar is normal for some extractors and simply means no tags.
    /// </summary>
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
