using System.Globalization;
using System.Text.Json;

namespace Boh.Web.Services;

/// <summary>Video via ffprobe and ffmpeg. Registered after the image processor.</summary>
public sealed class VideoMediaProcessor(
    ProcessRunner runner,
    ILogger<VideoMediaProcessor> logger) : IMediaProcessor
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ThumbnailTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Storable containers by ffprobe format name; others are refused.</summary>
    private static readonly Dictionary<string, (string Mime, string Extension)> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mov,mp4,m4a,3gp,3g2,mj2"] = ("video/mp4", ".mp4"),
        ["matroska,webm"] = ("video/webm", ".webm"),
    };

    public async Task<MediaInfo?> TryProbeAsync(string sourcePath, CancellationToken ct)
    {
        var result = await runner.RunAsync("ffprobe",
        [
            "-v", "error",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            "-select_streams", "v:0",
            sourcePath
        ], ProbeTimeout, ct);

        if (!result.Succeeded) return null;

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;

            if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0) return null;

            var stream = streams[0];
            if (!stream.TryGetProperty("width", out var widthElement) ||
                !stream.TryGetProperty("height", out var heightElement)) return null;

            var formatName = root.TryGetProperty("format", out var format)
                && format.TryGetProperty("format_name", out var name)
                    ? name.GetString() ?? ""
                    : "";

            if (!Allowed.TryGetValue(formatName, out var mapping))
            {
                logger.LogInformation("Rejected unsupported container {Format}", formatName);
                return null;
            }

            return new MediaInfo(
                Width: widthElement.GetInt32(),
                Height: heightElement.GetInt32(),
                MimeType: mapping.Mime,
                Extension: mapping.Extension,
                DurationSec: ReadDuration(root),
                IsVideo: true);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "ffprobe returned output that could not be parsed");
            return null;
        }
    }

    public async Task GenerateThumbnailAsync(
        string sourcePath, string destinationPath, int maxEdge, CancellationToken ct)
    {
        // One second in skips opening black frames; seeking before -i is fast.
        var result = await TryExtractFrameAsync(sourcePath, destinationPath, maxEdge, seekSeconds: 1, ct);

        // Clips shorter than that yield no frame, so fall back to the first.
        if (!Succeeded(result, destinationPath))
        {
            logger.LogDebug("Seeking 1s into {Path} produced no frame; retrying from the start", sourcePath);
            result = await TryExtractFrameAsync(sourcePath, destinationPath, maxEdge, seekSeconds: null, ct);
        }

        if (!Succeeded(result, destinationPath))
        {
            throw new InvalidOperationException(
                $"ffmpeg failed to produce a thumbnail (exit {result.ExitCode}): {result.StandardError.Trim()}");
        }
    }

    /// <summary>Video is not hashed: one frame doesn't represent a clip.</summary>
    public Task<long?> TryComputePerceptualHashAsync(string sourcePath, CancellationToken ct) =>
        Task.FromResult<long?>(null);

    private Task<ProcessResult> TryExtractFrameAsync(
        string sourcePath, string destinationPath, int maxEdge, int? seekSeconds, CancellationToken ct)
    {
        var args = new List<string> { "-v", "error" };

        if (seekSeconds is { } seek)
        {
            args.AddRange(["-noaccurate_seek", "-ss", TimeSpan.FromSeconds(seek).ToString(@"hh\:mm\:ss")]);
        }

        args.AddRange([
            "-i", sourcePath,
            "-frames:v", "1",
            "-vf", $"scale='min({maxEdge},iw)':'min({maxEdge},ih)':force_original_aspect_ratio=decrease",
            "-f", "webp",
            "-y", destinationPath
        ]);

        return runner.RunAsync("ffmpeg", args, ThumbnailTimeout, ct);
    }

    /// <summary>Exit code alone isn't enough; ffmpeg can leave a stub.</summary>
    private static bool Succeeded(ProcessResult result, string destinationPath)
    {
        if (!result.Succeeded) return false;

        var file = new FileInfo(destinationPath);

        return file.Exists && file.Length > 32;
    }

    private static double? ReadDuration(JsonElement root)
    {
        if (!root.TryGetProperty("format", out var format) ||
            !format.TryGetProperty("duration", out var duration)) return null;

        var text = duration.GetString();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;
    }
}
