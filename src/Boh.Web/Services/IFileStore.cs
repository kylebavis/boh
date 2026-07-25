namespace Boh.Web.Services;

/// <summary>An upload written to scratch space, with its content hash already known.</summary>
public sealed record StagedFile(string TempPath, string Sha256, long Length);

/// <summary>
/// Blob storage keyed by content hash. Paths depend only on the hash, never on a
/// database identity, so files can be written before any row exists and writes are
/// safely repeatable.
/// </summary>
public interface IFileStore
{
    void EnsureDirectories();

    /// <summary>Streams <paramref name="source"/> to scratch space, hashing as it goes.</summary>
    Task<StagedFile> StageAsync(Stream source, CancellationToken ct);

    /// <summary>Moves a staged file to its permanent location. A no-op if the blob already exists.</summary>
    void CommitOriginal(StagedFile staged, string extension);

    /// <summary>Removes a staged file that will not be committed. Safe to call twice.</summary>
    void Discard(StagedFile staged);

    string OriginalPath(string sha256, string extension);
    string ThumbPath(string sha256);

    bool OriginalExists(string sha256, string extension);
    bool ThumbExists(string sha256);

    /// <summary>Creates the parent directory for a thumbnail so an encoder can write into it.</summary>
    void EnsureThumbDirectory(string sha256);

    /// <summary>Deletes both the original and the thumbnail for a hash. Safe if either is missing.</summary>
    void DeleteBlobs(string sha256, string extension);

    /// <summary>Removes stale scratch files left behind by interrupted uploads.</summary>
    void CleanTemp(TimeSpan olderThan);
}
