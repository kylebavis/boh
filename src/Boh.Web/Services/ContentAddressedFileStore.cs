using System.Buffers;
using System.Security.Cryptography;

namespace Boh.Web.Services;

/// <summary>An upload written to scratch space, with its content hash already known.</summary>
public sealed record StagedFile(string TempPath, string Sha256, long Length);

/// <summary>
/// Blob storage keyed by content hash at <c>{root}/{aa}/{bb}/{sha256}{ext}</c>, so writes
/// need no row and are repeatable.
/// </summary>
public sealed class ContentAddressedFileStore(BohOptions options, ILogger<ContentAddressedFileStore> logger)
{
    private const int BufferSize = 81920;

    /// <summary>Below this a thumbnail cannot contain a decodable image.</summary>
    private const int MinimumUsableThumbnailBytes = 32;

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(options.OriginalsDir);
        Directory.CreateDirectory(options.ThumbsDir);
        Directory.CreateDirectory(options.UploadStagingDir);
    }

    /// <summary>Streams <paramref name="source"/> to scratch space, hashing as it goes.</summary>
    public async Task<StagedFile> StageAsync(Stream source, CancellationToken ct)
    {
        Directory.CreateDirectory(options.UploadStagingDir);
        var tempPath = Path.Combine(options.UploadStagingDir, $"upload-{Guid.NewGuid():N}.part");

        using var sha = SHA256.Create();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long total = 0;

        try
        {
            await using (var fs = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
                {
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;
                }
            }

            sha.TransformFinalBlock([], 0, 0);
            return new StagedFile(tempPath, Convert.ToHexStringLower(sha.Hash!), total);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Moves a staged file to its permanent location. A no-op if the blob already exists.</summary>
    public void CommitOriginal(StagedFile staged, string extension)
    {
        var destination = OriginalPath(staged.Sha256, extension);

        if (File.Exists(destination))
        {
            // Already stored.
            Discard(staged);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        try
        {
            File.Move(staged.TempPath, destination, overwrite: false);
        }
        catch (IOException) when (File.Exists(destination))
        {
            // A concurrent upload won; its copy is identical.
            Discard(staged);
        }
    }

    /// <summary>Removes a staged file that will not be committed. Safe to call twice.</summary>
    public void Discard(StagedFile staged) => TryDelete(staged.TempPath);

    public string OriginalPath(string sha256, string extension) =>
        Sharded(options.OriginalsDir, sha256, extension);

    public string ThumbPath(string sha256) =>
        Sharded(options.ThumbsDir, sha256, ".webp");

    public bool OriginalExists(string sha256, string extension) =>
        File.Exists(OriginalPath(sha256, extension));

    /// <summary>A usable thumbnail exists, not a truncated stub from a failed encode.</summary>
    public bool ThumbExists(string sha256)
    {
        var file = new FileInfo(ThumbPath(sha256));

        return file.Exists && file.Length > MinimumUsableThumbnailBytes;
    }

    public void EnsureThumbDirectory(string sha256) =>
        Directory.CreateDirectory(Path.GetDirectoryName(ThumbPath(sha256))!);

    public void DeleteBlobs(string sha256, string extension)
    {
        TryDelete(OriginalPath(sha256, extension));
        TryDelete(ThumbPath(sha256));
    }

    /// <summary>Removes stale scratch files left behind by interrupted uploads.</summary>
    public void CleanTemp(TimeSpan olderThan)
    {
        var cutoff = DateTime.UtcNow - olderThan;

        // Also sweeps the import directory, where staging used to live.
        foreach (var directory in new[] { options.UploadStagingDir, options.ImportTempDir })
        {
            if (!Directory.Exists(directory)) continue;

            foreach (var path in Directory.EnumerateFiles(directory, "*.part"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
                }
                catch (IOException) { /* in use or already gone */ }
            }
        }
    }

    private static string Sharded(string root, string sha256, string suffix)
    {
        if (sha256.Length < 4)
            throw new ArgumentException("Hash is too short to shard.", nameof(sha256));

        return Path.Combine(root, sha256[..2], sha256[2..4], sha256 + suffix);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not delete {Path}", path);
        }
    }
}
