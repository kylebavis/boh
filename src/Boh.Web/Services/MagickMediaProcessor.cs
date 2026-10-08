using ImageMagick;

namespace Boh.Web.Services;

/// <summary>
/// Still images via ImageMagick, identified by magic bytes. Decoding untrusted input in native
/// code, so formats are allowlisted and resources capped.
/// </summary>
public sealed class MagickMediaProcessor(ILogger<MagickMediaProcessor> logger) : IMediaProcessor
{
    /// <summary>Decodable formats, with storage MIME type and extension.</summary>
    private static readonly Dictionary<MagickFormat, (string Mime, string Extension)> Allowed = new()
    {
        [MagickFormat.Jpeg] = ("image/jpeg", ".jpg"),
        [MagickFormat.Jpg] = ("image/jpeg", ".jpg"),
        [MagickFormat.Png] = ("image/png", ".png"),
        [MagickFormat.Png00] = ("image/png", ".png"),
        [MagickFormat.Png8] = ("image/png", ".png"),
        [MagickFormat.Png24] = ("image/png", ".png"),
        [MagickFormat.Png32] = ("image/png", ".png"),
        [MagickFormat.Png48] = ("image/png", ".png"),
        [MagickFormat.Png64] = ("image/png", ".png"),
        [MagickFormat.Gif] = ("image/gif", ".gif"),
        [MagickFormat.Gif87] = ("image/gif", ".gif"),
        [MagickFormat.WebP] = ("image/webp", ".webp"),
        [MagickFormat.Bmp] = ("image/bmp", ".bmp"),
        [MagickFormat.Bmp2] = ("image/bmp", ".bmp"),
        [MagickFormat.Bmp3] = ("image/bmp", ".bmp"),
        [MagickFormat.Tiff] = ("image/tiff", ".tif"),
        [MagickFormat.Tiff64] = ("image/tiff", ".tif"),
        [MagickFormat.Avif] = ("image/avif", ".avif"),
        [MagickFormat.Heic] = ("image/heic", ".heic"),
        [MagickFormat.Heif] = ("image/heif", ".heif"),
    };

    /// <summary>Process-wide decode limits, applied once at startup.</summary>
    public static void ApplyResourceLimits()
    {
        ResourceLimits.Width = 50_000;
        ResourceLimits.Height = 50_000;
        ResourceLimits.Memory = 512 * 1024 * 1024;      // spill to disk beyond this
        ResourceLimits.Disk = 2L * 1024 * 1024 * 1024;  // then fail rather than fill the volume
        ResourceLimits.ListLength = 512;                // caps frames in animated input
    }

    public Task<MediaInfo?> TryProbeAsync(string sourcePath, CancellationToken ct)
    {
        try
        {
            // Header only.
            var info = new MagickImageInfo(sourcePath);

            if (!Allowed.TryGetValue(info.Format, out var mapping))
            {
                logger.LogInformation("Rejected unsupported image format {Format}", info.Format);
                return Task.FromResult<MediaInfo?>(null);
            }

            return Task.FromResult<MediaInfo?>(new MediaInfo(
                Width: (int)info.Width,
                Height: (int)info.Height,
                MimeType: mapping.Mime,
                Extension: mapping.Extension,
                DurationSec: null,
                IsVideo: false));
        }
        catch (MagickException)
        {
            return Task.FromResult<MediaInfo?>(null);
        }
    }

    public async Task GenerateThumbnailAsync(
        string sourcePath, string destinationPath, int maxEdge, CancellationToken ct)
    {
        // Single frame, so animations thumbnail from frame one.
        using var image = new MagickImage(sourcePath);

        image.AutoOrient();     // honor EXIF rotation before resizing
        image.Strip();          // drop EXIF/GPS: thumbnails are public surface

        // Shrink only; never upscale.
        image.Resize(new MagickGeometry((uint)maxEdge, (uint)maxEdge) { Greater = true });

        image.Format = MagickFormat.WebP;
        image.Quality = 82;

        await image.WriteAsync(destinationPath, ct);
    }

    /// <summary>
    /// Hashes the original, so the hash doesn't depend on thumbnail size. Our DCT hash, not
    /// Magick's <see cref="PerceptualHash"/>.
    /// </summary>
    public Task<long?> TryComputePerceptualHashAsync(string sourcePath, CancellationToken ct)
    {
        try
        {
            using var image = new MagickImage(sourcePath);

            image.AutoOrient();     // hash what a viewer sees, not how the file happens to be stored

            // Flatten alpha onto white so a PNG and its JPEG agree.
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaOption.Remove);

            image.Grayscale();

            // Squash to a square: describe the picture, not its shape.
            var edge = (uint)Media.PerceptualHash.GridEdge;
            image.Resize(new MagickGeometry(edge, edge) { IgnoreAspectRatio = true });

            // Q8 grayscale: the red channel is the luminance.
            using var pixels = image.GetPixels();
            var grayscale = pixels.ToByteArray("R");

            return Task.FromResult(grayscale is null ? null : Media.PerceptualHash.TryCompute(grayscale));
        }
        catch (MagickException ex)
        {
            // Probes as an image but won't decode. A missing hash is not fatal.
            logger.LogWarning(ex, "Could not decode {Path} to hash it perceptually", sourcePath);
            return Task.FromResult<long?>(null);
        }
    }
}
