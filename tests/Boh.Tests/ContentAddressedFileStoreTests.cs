using System.IO;
using System.Security.Cryptography;

namespace Boh.Tests;

public class ContentAddressedFileStoreTests
{
    [Test]
    public async Task StageAsync_computes_the_same_hash_as_the_content()
    {
        using var env = new TestEnvironment();
        var bytes = TestEnvironment.MakePng(32, 32);
        var expected = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var staged = await env.Store.StageAsync(new MemoryStream(bytes), TestContext.Current!.Execution.CancellationToken);

        await Assert.That(staged.Sha256).IsEqualTo(expected);
        await Assert.That(staged.Length).IsEqualTo(bytes.Length);
        await Assert.That(File.Exists(staged.TempPath)).IsTrue();
    }

    [Test]
    public async Task CommitOriginal_places_the_blob_in_a_two_level_shard()
    {
        using var env = new TestEnvironment();
        var bytes = TestEnvironment.MakePng(16, 16);

        var staged = await env.Store.StageAsync(new MemoryStream(bytes), TestContext.Current!.Execution.CancellationToken);
        env.Store.CommitOriginal(staged, ".png");

        var expected = Path.Combine(
            env.Options.OriginalsDir,
            staged.Sha256[..2],
            staged.Sha256[2..4],
            staged.Sha256 + ".png");

        await Assert.That(File.Exists(expected)).IsTrue();
        await Assert.That(await File.ReadAllBytesAsync(expected, TestContext.Current!.Execution.CancellationToken)).IsEquivalentTo(bytes, CollectionOrdering.Matching);
        await Assert.That(File.Exists(staged.TempPath)).IsFalse();
    }

    [Test]
    public async Task CommitOriginal_is_idempotent_for_identical_content()
    {
        using var env = new TestEnvironment();
        var bytes = TestEnvironment.MakePng(16, 16);
        var ct = TestContext.Current!.Execution.CancellationToken;

        var first = await env.Store.StageAsync(new MemoryStream(bytes), ct);
        env.Store.CommitOriginal(first, ".png");

        // A second upload of the same bytes must not fail or duplicate the blob.
        var second = await env.Store.StageAsync(new MemoryStream(bytes), ct);
        env.Store.CommitOriginal(second, ".png");

        await Assert.That(second.Sha256).IsEqualTo(first.Sha256);
        await Assert.That(File.Exists(second.TempPath)).IsFalse();
        await Assert.That(Directory.EnumerateFiles(env.Options.OriginalsDir, "*.png", SearchOption.AllDirectories)).HasSingleItem();
    }

    [Test]
    public async Task DeleteBlobs_removes_both_original_and_thumbnail()
    {
        using var env = new TestEnvironment();
        var ct = TestContext.Current!.Execution.CancellationToken;

        var staged = await env.Store.StageAsync(new MemoryStream(TestEnvironment.MakePng(16, 16)), ct);
        env.Store.CommitOriginal(staged, ".png");
        env.Store.EnsureThumbDirectory(staged.Sha256);
        await File.WriteAllTextAsync(env.Store.ThumbPath(staged.Sha256), "placeholder", ct);

        env.Store.DeleteBlobs(staged.Sha256, ".png");

        await Assert.That(env.Store.OriginalExists(staged.Sha256, ".png")).IsFalse();
        await Assert.That(env.Store.ThumbExists(staged.Sha256)).IsFalse();
    }

    [Test]
    public async Task CleanTemp_leaves_recent_staging_files_alone()
    {
        using var env = new TestEnvironment();
        var staged = await env.Store.StageAsync(
            new MemoryStream(TestEnvironment.MakePng(8, 8)), TestContext.Current!.Execution.CancellationToken);

        env.Store.CleanTemp(TimeSpan.FromHours(6));

        await Assert.That(File.Exists(staged.TempPath)).IsTrue();
    }
}
