namespace Boh.Web.Services;

/// <summary>
/// Facts about a media file, determined by inspecting its content rather than its name.
/// <paramref name="Extension"/> is the canonical extension for the detected format, which
/// is why a <c>.png</c> that is really a JPEG gets stored as <c>.jpg</c>.
/// </summary>
public sealed record MediaInfo(
    int Width,
    int Height,
    string MimeType,
    string Extension,
    double? DurationSec,
    bool IsVideo);

public interface IMediaProcessor
{
    /// <summary>
    /// Returns details if this processor recognizes the file, otherwise null.
    /// Returning null is the normal "not mine" signal and must not throw.
    /// </summary>
    Task<MediaInfo?> TryProbeAsync(string sourcePath, CancellationToken ct);

    Task GenerateThumbnailAsync(string sourcePath, string destinationPath, int maxEdge, CancellationToken ct);
}

/// <summary>Picks the first registered processor that recognizes a file.</summary>
public sealed class MediaProcessorRegistry(IEnumerable<IMediaProcessor> processors)
{
    public async Task<(IMediaProcessor Processor, MediaInfo Info)?> ProbeAsync(string path, CancellationToken ct)
    {
        foreach (var processor in processors)
        {
            var info = await processor.TryProbeAsync(path, ct);
            if (info is not null) return (processor, info);
        }

        return null;
    }
}
