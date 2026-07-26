namespace Boh.Tests;

public class ThumbnailRepairTests
{
    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task A_deleted_thumbnail_is_rebuilt()
    {
        using var env = new TestEnvironment();
        var postId = await env.CreatePostAsync();

        var post = (await env.Posts.GetAsync(postId, Ct))!;
        var thumbPath = env.Store.ThumbPath(post.Sha256);
        Assert.True(File.Exists(thumbPath));

        File.Delete(thumbPath);
        Assert.False(env.Store.ThumbExists(post.Sha256));

        var result = await env.Posts.RegenerateMissingThumbnailsAsync(Ct);

        Assert.Equal(1, result.Missing);
        Assert.Equal(1, result.Regenerated);
        Assert.Equal(0, result.Failed);
        Assert.True(result.Complete);
        Assert.True(File.Exists(thumbPath));
    }

    [Fact]
    public async Task The_rebuilt_thumbnail_is_a_real_image()
    {
        using var env = new TestEnvironment();
        var postId = await env.CreatePostAsync();
        var post = (await env.Posts.GetAsync(postId, Ct))!;
        var thumbPath = env.Store.ThumbPath(post.Sha256);

        File.Delete(thumbPath);
        await env.Posts.RegenerateMissingThumbnailsAsync(Ct);

        // RIFF....WEBP — proves an encoder actually ran rather than a file being touched.
        var header = File.ReadAllBytes(thumbPath).AsSpan(0, 12);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(header[..4]));
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(header[8..12]));
    }

    [Fact]
    public async Task Posts_that_still_have_a_thumbnail_are_left_alone()
    {
        using var env = new TestEnvironment();
        await env.CreatePostAsync(30);
        await env.CreatePostAsync(31);

        var result = await env.Posts.RegenerateMissingThumbnailsAsync(Ct);

        Assert.Equal(0, result.Missing);
        Assert.Equal(0, result.Regenerated);
        Assert.True(result.Complete);
    }

    [Fact]
    public async Task Only_the_missing_thumbnails_are_rebuilt()
    {
        using var env = new TestEnvironment();
        var first = await env.CreatePostAsync(32);
        await env.CreatePostAsync(33);

        var post = (await env.Posts.GetAsync(first, Ct))!;
        File.Delete(env.Store.ThumbPath(post.Sha256));

        var result = await env.Posts.RegenerateMissingThumbnailsAsync(Ct);

        Assert.Equal(1, result.Missing);
        Assert.Equal(1, result.Regenerated);
    }

    /// <summary>
    /// The originals volume being unavailable is the realistic failure once media lives on a
    /// separate mount, so it must be reported rather than thrown.
    /// </summary>
    [Fact]
    public async Task A_post_whose_original_is_gone_is_reported_not_thrown()
    {
        using var env = new TestEnvironment();
        var postId = await env.CreatePostAsync();
        var post = (await env.Posts.GetAsync(postId, Ct))!;

        File.Delete(env.Store.ThumbPath(post.Sha256));
        File.Delete(env.Store.OriginalPath(post.Sha256, post.FileExtension));

        var result = await env.Posts.RegenerateMissingThumbnailsAsync(Ct);

        Assert.Equal(1, result.Missing);
        Assert.Equal(0, result.Regenerated);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task An_unreadable_original_is_reported_not_thrown()
    {
        using var env = new TestEnvironment();
        var postId = await env.CreatePostAsync();
        var post = (await env.Posts.GetAsync(postId, Ct))!;

        File.Delete(env.Store.ThumbPath(post.Sha256));

        // Replace the original with bytes no processor will recognize.
        File.WriteAllText(env.Store.OriginalPath(post.Sha256, post.FileExtension), "not an image");

        var result = await env.Posts.RegenerateMissingThumbnailsAsync(Ct);

        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Regenerated);
    }

    [Fact]
    public async Task Running_it_twice_is_a_no_op_the_second_time()
    {
        using var env = new TestEnvironment();
        var postId = await env.CreatePostAsync();
        var post = (await env.Posts.GetAsync(postId, Ct))!;
        File.Delete(env.Store.ThumbPath(post.Sha256));

        await env.Posts.RegenerateMissingThumbnailsAsync(Ct);
        var second = await env.Posts.RegenerateMissingThumbnailsAsync(Ct);

        Assert.Equal(0, second.Missing);
        Assert.Equal(0, second.Regenerated);
    }

    /// <summary>
    /// A failed encode leaves a truncated file rather than nothing. If that counted as a
    /// thumbnail, the repair pass would skip exactly the posts it exists to fix — which is
    /// what happened to 8 short video clips during a real migration.
    /// </summary>
    [Fact]
    public async Task A_truncated_stub_counts_as_missing_and_is_regenerated()
    {
        using var env = new TestEnvironment();
        var postId = await env.CreatePostAsync();
        var post = (await env.Posts.GetAsync(postId, Ct))!;
        var thumbPath = env.Store.ThumbPath(post.Sha256);

        // Exactly what ffmpeg left behind: a few bytes, no decodable image.
        await File.WriteAllBytesAsync(thumbPath, new byte[8]);
        Assert.False(env.Store.ThumbExists(post.Sha256));

        var result = await env.Posts.RegenerateMissingThumbnailsAsync(Ct);

        Assert.Equal(1, result.Missing);
        Assert.Equal(1, result.Regenerated);
        Assert.True(new FileInfo(thumbPath).Length > 32);
    }

    [Fact]
    public async Task An_empty_collection_completes_cleanly()
    {
        using var env = new TestEnvironment();

        var result = await env.Posts.RegenerateMissingThumbnailsAsync(Ct);

        Assert.Equal(0, result.Missing);
        Assert.True(result.Complete);
    }
}
