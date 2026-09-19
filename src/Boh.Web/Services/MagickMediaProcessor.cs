using ImageMagick;

namespace Boh.Web.Services;

/// <summary>
/// Handles still images via ImageMagick, which identifies formats from magic bytes —
/// nothing here trusts the uploaded filename.
/// </summary>
/// <remarks>
/// ImageMagick decodes untrusted input in native code, so this class deliberately
/// narrows what reaches it: only formats on <see cref="Allowed"/> are accepted, and
/// <see cref="ApplyResourceLimits"/> caps what a single decode may consume.
/// </remarks>
public sealed class MagickMediaProcessor(ILogger<MagickMediaProcessor> logger) : IMediaProcessor
{
    /// <summary>
    /// Formats we are willing to decode, mapped to the MIME type and extension used for
    /// storage. Anything absent is rejected rather than handed to a less-exercised coder.
    /// </summary>
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

    /// <summary>
    /// Bounds a single decode so a malicious file cannot exhaust the host. These are
    /// process-wide ImageMagick settings and only need applying once at startup.
    /// </summary>
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
            // Reads the header only; the pixel data is never decoded here.
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
            // Not an image ImageMagick recognizes; another processor may claim it.
            return Task.FromResult<MediaInfo?>(null);
        }
    }

    public async Task GenerateThumbnailAsync(
        string sourcePath, string destinationPath, int maxEdge, CancellationToken ct)
    {
        // MagickImage reads a single frame, so animated sources thumbnail from frame one.
        using var image = new MagickImage(sourcePath);

        image.AutoOrient();     // honour EXIF rotation before resizing
        image.Strip();          // drop EXIF/GPS: thumbnails are public surface

        // The '>' geometry flag shrinks oversized images and leaves smaller ones alone,
        // so a 50x50 source never becomes a blurry upscale.
        image.Resize(new MagickGeometry((uint)maxEdge, (uint)maxEdge) { Greater = true });

        image.Format = MagickFormat.WebP;
        image.Quality = 82;

        await image.WriteAsync(destinationPath, ct);
    }

    /// <summary>
    /// Hashes the original rather than the thumbnail, so the hash does not shift with
    /// <c>BOH_THUMBNAIL_SIZE</c> and stays comparable across instances.
    /// </summary>
    /// <remarks>
    /// Not ImageMagick's own <see cref="PerceptualHash"/>, which is why the type is spelled
    /// out here. That one is a set of image moments per colour channel, compared by summed
    /// squared distance: several hundred characters to store, a floating-point threshold with
    /// no natural scale to pick it on, and no way to ask "within N of this" in a query. The
    /// DCT hash in <see cref="Media.PerceptualHash"/> is eight bytes and a bit count.
    /// </remarks>
    public Task<long?> TryComputePerceptualHashAsync(string sourcePath, CancellationToken ct)
    {
        try
        {
            using var image = new MagickImage(sourcePath);

            image.AutoOrient();     // hash what a viewer sees, not how the file happens to be stored

            // Transparent pixels have no brightness of their own, and Magick leaves them
            // arbitrary. Compositing onto a fixed background first means a PNG and the JPEG
            // someone made of it — which had nowhere to put the alpha but white — agree.
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaOption.Remove);

            image.Grayscale();

            // IgnoreAspectRatio deliberately squashes the image into a square: the hash has to
            // describe the picture, not its shape, or a crop of one edge would move every bit.
            var edge = (uint)Media.PerceptualHash.GridEdge;
            image.Resize(new MagickGeometry(edge, edge) { IgnoreAspectRatio = true });

            // Q8 makes one channel one byte, and greyscale collapses the three into the same
            // value, so the red channel alone is the luminance grid the hash wants.
            using var pixels = image.GetPixels();
            var greyscale = pixels.ToByteArray("R");

            return Task.FromResult(greyscale is null ? null : Media.PerceptualHash.TryCompute(greyscale));
        }
        catch (MagickException ex)
        {
            // The file probed as an image but will not decode. A post without a hash is only
            // missing near-duplicate detection, so this is a warning rather than a failure.
            logger.LogWarning(ex, "Could not decode {Path} to hash it perceptually", sourcePath);
            return Task.FromResult<long?>(null);
        }
    }
}
