using System.Globalization;
using System.Text.Json;

namespace Boh.Web.Services;

/// <summary>
/// Handles video by shelling out to ffprobe and ffmpeg. Registered after the image
/// processor, so it only sees files ImageMagick declined.
/// </summary>
public sealed class VideoMediaProcessor(
    ProcessRunner runner,
    ILogger<VideoMediaProcessor> logger) : IMediaProcessor
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ThumbnailTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Containers we are willing to store, keyed by the format name ffprobe reports.
    /// ffprobe recognizes far more than this; anything unlisted is refused rather than
    /// stored in a format browsers cannot play.
    /// </summary>
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
        // Seek before -i so ffmpeg jumps straight there instead of decoding from the start.
        // One second in avoids the black frame many videos open on; -noaccurate_seek keeps
        // it fast, and clips shorter than a second still yield their first frame.
        var result = await runner.RunAsync("ffmpeg",
        [
            "-v", "error",
            "-noaccurate_seek",
            "-ss", "00:00:01",
            "-i", sourcePath,
            "-frames:v", "1",
            "-vf", $"scale='min({maxEdge},iw)':'min({maxEdge},ih)':force_original_aspect_ratio=decrease",
            "-f", "webp",
            "-y", destinationPath
        ], ThumbnailTimeout, ct);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"ffmpeg failed to produce a thumbnail (exit {result.ExitCode}): {result.StandardError.Trim()}");
        }
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
