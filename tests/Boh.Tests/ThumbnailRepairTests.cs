using System.IO;

namespace Boh.Tests;

public class ThumbnailRepairTests
{
    private static CancellationToken Ct => TestContext.Current!.Execution.CancellationToken;

    [Test]
    public async Task A_deleted_thumbnail_is_rebuilt()
    {
        using var env = new TestEnvironment();
        var postId = await env.CreatePostAsync();

        var post = (await env.Posts.GetAsync(postId, Ct))!;
        var thumbPath = env.Store.ThumbPath(post.Sha256);
        await Assert.That(File.Exists(thumbPath)).IsTrue();

        File.Delete(thumbPath);
        await Assert.That(env.Store.ThumbExists(post.Sha256)).IsFalse();

        var result = await env.Posts.RegenerateMissingThumbnailsAsync(null, Ct);

        await Assert.That(result.Missing).IsEqualTo(1);
        await Assert.That(result.Regenerated).IsEqualTo(1);
        await Assert.That(result.Failed).IsEqualTo(0);
        await Assert.That(File.Exists(thumbPath)).IsTrue();
    }

    [Test]
    public async Task The_rebuilt_thumbnail_is_a_real_image()
    {
        using var env = new TestEnvironment();
        var postId = await env.CreatePostAsync();
        var post = (await env.Posts.GetAsync(postId, Ct))!;
        var thumbPath = env.Store.ThumbPath(post.Sha256);

        File.Delete(thumbPath);
        await env.Posts.RegenerateMissingThumbnailsAsync(null, Ct);

        // RIFF....WEBP — proves an encoder actually ran rather than a file being touched.
        var header = File.ReadAllBytes(thumbPath)[..12];
        await Assert.That(System.Text.Encoding.ASCII.GetString(header[..4])).IsEqualTo("RIFF");
        await Assert.That(System.Text.Encoding.ASCII.GetString(header[8..12])).IsEqualTo("WEBP");
    }

    [Test]
    public async Task Posts_that_still_have_a_thumbnail_are_left_alone()
    {
        using var env = new TestEnvironment();
        await env.CreatePostAsync(30);
        await env.CreatePostAsync(31);

        var result = await env.Posts.RegenerateMissingThumbnailsAsync(null, Ct);

        await Assert.That(result.Missing).IsEqualTo(0);
        await Assert.That(result.Regenerated).IsEqualTo(0);
    }

    [Test]
    public async Task Only_the_missing_thumbnails_are_rebuilt()
    {
        using var env = new TestEnvironment();
        var first = await env.CreatePostAsync(32);
        await env.CreatePostAsync(33);

        var post = (await env.Posts.GetAsync(first, Ct))!;
        File.Delete(env.Store.ThumbPath(post.Sha256));

        var result = await env.Posts.RegenerateMissingThumbnailsAsync(null, Ct);

        await Assert.That(result.Missing).IsEqualTo(1);
        await Assert.That(result.Regenerated).IsEqualTo(1);
    }

    /// <summary>
    /// The originals volume being unavailable is the realistic failure once media lives on a
    /// separate mount, so it must be reported rather than thrown.
    /// </summary>
    [Test]
    public async Task A_post_whose_original_is_gone_is_reported_not_thrown()
    {
        using var env = new TestEnvironment();
        var postId = await env.CreatePostAsync();
        var post = (await env.Posts.GetAsync(postId, Ct))!;

        File.Delete(env.Store.ThumbPath(post.Sha256));
        File.Delete(env.Store.OriginalPath(post.Sha256, post.FileExtension));

        var result = await env.Posts.RegenerateMissingThumbnailsAsync(null, Ct);

        await Assert.That(result.Missing).IsEqualTo(1);
        await Assert.That(result.Regenerated).IsEqualTo(0);
        await Assert.That(result.Failed).IsEqualTo(1);
    }

    [Test]
    public async Task An_unreadable_original_is_reported_not_thrown()
    {
        using var env = new TestEnvironment();
        var postId = await env.CreatePostAsync();
        var post = (await env.Posts.GetAsync(postId, Ct))!;

        File.Delete(env.Store.ThumbPath(post.Sha256));

        // Replace the original with bytes no processor will recognize.
        File.WriteAllText(env.Store.OriginalPath(post.Sha256, post.FileExtension), "not an image");

        var result = await env.Posts.RegenerateMissingThumbnailsAsync(null, Ct);

        await Assert.That(result.Failed).IsEqualTo(1);
        await Assert.That(result.Regenerated).IsEqualTo(0);
    }

    [Test]
    public async Task Running_it_twice_is_a_no_op_the_second_time()
    {
        using var env = new TestEnvironment();
        var postId = await env.CreatePostAsync();
        var post = (await env.Posts.GetAsync(postId, Ct))!;
        File.Delete(env.Store.ThumbPath(post.Sha256));

        await env.Posts.RegenerateMissingThumbnailsAsync(null, Ct);
        var second = await env.Posts.RegenerateMissingThumbnailsAsync(null, Ct);

        await Assert.That(second.Missing).IsEqualTo(0);
        await Assert.That(second.Regenerated).IsEqualTo(0);
    }

    /// <summary>
    /// A failed encode leaves a truncated file rather than nothing. If that counted as a
    /// thumbnail, the repair pass would skip exactly the posts it exists to fix — which is
    /// what happened to 8 short video clips during a real migration.
    /// </summary>
    [Test]
    public async Task A_truncated_stub_counts_as_missing_and_is_regenerated()
    {
        using var env = new TestEnvironment();
        var postId = await env.CreatePostAsync();
        var post = (await env.Posts.GetAsync(postId, Ct))!;
        var thumbPath = env.Store.ThumbPath(post.Sha256);

        // Exactly what ffmpeg left behind: a few bytes, no decodable image.
        await File.WriteAllBytesAsync(thumbPath, new byte[8], Ct);
        await Assert.That(env.Store.ThumbExists(post.Sha256)).IsFalse();

        var result = await env.Posts.RegenerateMissingThumbnailsAsync(null, Ct);

        await Assert.That(result.Missing).IsEqualTo(1);
        await Assert.That(result.Regenerated).IsEqualTo(1);
        await Assert.That(new FileInfo(thumbPath).Length > 32).IsTrue();
    }

    [Test]
    public async Task An_empty_collection_completes_cleanly()
    {
        using var env = new TestEnvironment();

        var result = await env.Posts.RegenerateMissingThumbnailsAsync(null, Ct);

        await Assert.That(result.Missing).IsEqualTo(0);
    }

    /// <summary>
    /// Progress counts posts checked, not thumbnails rebuilt: how many are missing is only known
    /// by the end, while how many there are to check is known from the start.
    /// </summary>
    [Test]
    public async Task Progress_counts_through_every_post_and_ends_complete()
    {
        using var env = new TestEnvironment();
        await env.CreatePostAsync(30);
        await env.CreatePostAsync(31);
        var progress = new ProgressRecorder();

        await env.Posts.RegenerateMissingThumbnailsAsync(progress, Ct);

        var last = progress.Reports[^1];
        await Assert.That(last.Done).IsEqualTo(2L);
        await Assert.That(last.Total).IsEqualTo((long?)2);
    }
}
