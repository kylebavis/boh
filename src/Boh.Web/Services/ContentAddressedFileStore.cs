using System.Buffers;
using System.Security.Cryptography;

namespace Boh.Web.Services;

/// <summary>
/// Stores blobs at <c>{root}/{aa}/{bb}/{sha256}{ext}</c>. The two shard levels keep any
/// single directory to a few thousand entries at collection sizes this project targets,
/// which matters for filesystems that degrade on very wide directories.
/// </summary>
public sealed class ContentAddressedFileStore(BohOptions options, ILogger<ContentAddressedFileStore> logger)
    : IFileStore
{
    private const int BufferSize = 81920;

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(options.OriginalsDir);
        Directory.CreateDirectory(options.ThumbsDir);
        Directory.CreateDirectory(options.UploadStagingDir);
    }

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

    public void CommitOriginal(StagedFile staged, string extension)
    {
        var destination = OriginalPath(staged.Sha256, extension);

        if (File.Exists(destination))
        {
            // Identical content already stored — the staged copy adds nothing.
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
            // A concurrent upload of the same content won the race; its copy is equivalent.
            Discard(staged);
        }
    }

    public void Discard(StagedFile staged) => TryDelete(staged.TempPath);

    public string OriginalPath(string sha256, string extension) =>
        Sharded(options.OriginalsDir, sha256, extension);

    public string ThumbPath(string sha256) =>
        Sharded(options.ThumbsDir, sha256, ".webp");

    public bool OriginalExists(string sha256, string extension) =>
        File.Exists(OriginalPath(sha256, extension));

    public bool ThumbExists(string sha256) => File.Exists(ThumbPath(sha256));

    public void EnsureThumbDirectory(string sha256) =>
        Directory.CreateDirectory(Path.GetDirectoryName(ThumbPath(sha256))!);

    public void DeleteBlobs(string sha256, string extension)
    {
        TryDelete(OriginalPath(sha256, extension));
        TryDelete(ThumbPath(sha256));
    }

    public void CleanTemp(TimeSpan olderThan)
    {
        var cutoff = DateTime.UtcNow - olderThan;

        // The import scratch directory is swept too because staging used to live there.
        // Upgrading an existing install would otherwise strand any .part files an
        // interrupted upload left behind, with nothing looking at that path again.
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
