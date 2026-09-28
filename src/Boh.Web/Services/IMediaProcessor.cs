namespace Boh.Web.Services;

/// <summary>Facts from a file's content. <paramref name="Extension"/> is canonical for the detected format.</summary>
public sealed record MediaInfo(
    int Width,
    int Height,
    string MimeType,
    string Extension,
    double? DurationSec,
    bool IsVideo);

public interface IMediaProcessor
{
    /// <summary>Null means "not mine"; must not throw.</summary>
    Task<MediaInfo?> TryProbeAsync(string sourcePath, CancellationToken ct);

    Task GenerateThumbnailAsync(string sourcePath, string destinationPath, int maxEdge, CancellationToken ct);

    /// <summary>See <see cref="Media.PerceptualHash"/>. Null when there's nothing to hash; must not throw.</summary>
    Task<long?> TryComputePerceptualHashAsync(string sourcePath, CancellationToken ct);
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
