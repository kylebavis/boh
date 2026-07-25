using System.Security.Cryptography;

namespace Boh.Tests;

public class ContentAddressedFileStoreTests
{
    [Fact]
    public async Task StageAsync_computes_the_same_hash_as_the_content()
    {
        using var env = new TestEnvironment();
        var bytes = TestEnvironment.MakePng(32, 32);
        var expected = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var staged = await env.Store.StageAsync(new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(expected, staged.Sha256);
        Assert.Equal(bytes.Length, staged.Length);
        Assert.True(File.Exists(staged.TempPath));
    }

    [Fact]
    public async Task CommitOriginal_places_the_blob_in_a_two_level_shard()
    {
        using var env = new TestEnvironment();
        var bytes = TestEnvironment.MakePng(16, 16);

        var staged = await env.Store.StageAsync(new MemoryStream(bytes), CancellationToken.None);
        env.Store.CommitOriginal(staged, ".png");

        var expected = Path.Combine(
            env.Options.OriginalsDir,
            staged.Sha256[..2],
            staged.Sha256[2..4],
            staged.Sha256 + ".png");

        Assert.True(File.Exists(expected));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(expected, CancellationToken.None));
        Assert.False(File.Exists(staged.TempPath));
    }

    [Fact]
    public async Task CommitOriginal_is_idempotent_for_identical_content()
    {
        using var env = new TestEnvironment();
        var bytes = TestEnvironment.MakePng(16, 16);
        var ct = CancellationToken.None;

        var first = await env.Store.StageAsync(new MemoryStream(bytes), ct);
        env.Store.CommitOriginal(first, ".png");

        // A second upload of the same bytes must not fail or duplicate the blob.
        var second = await env.Store.StageAsync(new MemoryStream(bytes), ct);
        env.Store.CommitOriginal(second, ".png");

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.False(File.Exists(second.TempPath));
        Assert.Single(Directory.EnumerateFiles(env.Options.OriginalsDir, "*.png", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DeleteBlobs_removes_both_original_and_thumbnail()
    {
        using var env = new TestEnvironment();
        var ct = CancellationToken.None;

        var staged = await env.Store.StageAsync(new MemoryStream(TestEnvironment.MakePng(16, 16)), ct);
        env.Store.CommitOriginal(staged, ".png");
        env.Store.EnsureThumbDirectory(staged.Sha256);
        await File.WriteAllTextAsync(env.Store.ThumbPath(staged.Sha256), "placeholder", ct);

        env.Store.DeleteBlobs(staged.Sha256, ".png");

        Assert.False(env.Store.OriginalExists(staged.Sha256, ".png"));
        Assert.False(env.Store.ThumbExists(staged.Sha256));
    }

    [Fact]
    public async Task CleanTemp_leaves_recent_staging_files_alone()
    {
        using var env = new TestEnvironment();
        var staged = await env.Store.StageAsync(
            new MemoryStream(TestEnvironment.MakePng(8, 8)), CancellationToken.None);

        env.Store.CleanTemp(TimeSpan.FromHours(6));

        Assert.True(File.Exists(staged.TempPath));
    }
}
