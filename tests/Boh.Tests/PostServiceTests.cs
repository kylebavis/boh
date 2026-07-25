using System.Text;
using Boh.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Boh.Tests;

public class PostServiceTests
{
    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task CreateAsync_stores_the_post_and_generates_a_thumbnail()
    {
        using var env = new TestEnvironment();

        var result = await env.Posts.CreateAsync(
            new MemoryStream(TestEnvironment.MakePng(300, 200)), null, "", Ct);

        var created = Assert.IsType<PostCreateResult.Created>(result);
        Assert.Equal(300, created.Post.Width);
        Assert.Equal(200, created.Post.Height);
        Assert.Equal("image/png", created.Post.MimeType);
        Assert.Equal(".png", created.Post.FileExtension);
        Assert.False(created.Post.IsVideo);

        Assert.True(env.Store.OriginalExists(created.Post.Sha256, ".png"));
        Assert.True(env.Store.ThumbExists(created.Post.Sha256));
    }

    [Fact]
    public async Task CreateAsync_reports_a_duplicate_instead_of_storing_twice()
    {
        using var env = new TestEnvironment();
        var bytes = TestEnvironment.MakePng(120, 120);

        var first = Assert.IsType<PostCreateResult.Created>(
            await env.Posts.CreateAsync(new MemoryStream(bytes), null, "", Ct));

        var second = await env.Posts.CreateAsync(new MemoryStream(bytes), null, "", Ct);

        var duplicate = Assert.IsType<PostCreateResult.Duplicate>(second);
        Assert.Equal(first.Post.Id, duplicate.ExistingPostId);
        Assert.Equal(1, await env.Db.Posts.CountAsync(Ct));
    }

    [Fact]
    public async Task CreateAsync_rejects_content_that_is_not_an_image()
    {
        using var env = new TestEnvironment();
        var bytes = Encoding.UTF8.GetBytes("this is not an image, it is prose");

        var result = await env.Posts.CreateAsync(new MemoryStream(bytes), null, "", Ct);

        Assert.IsType<PostCreateResult.Rejected>(result);
        Assert.Equal(0, await env.Db.Posts.CountAsync(Ct));
    }

    [Fact]
    public async Task CreateAsync_rejects_an_empty_file()
    {
        using var env = new TestEnvironment();

        var result = await env.Posts.CreateAsync(new MemoryStream([]), null, "", Ct);

        Assert.IsType<PostCreateResult.Rejected>(result);
    }

    [Fact]
    public async Task CreateAsync_trusts_content_over_the_declared_format()
    {
        using var env = new TestEnvironment();

        // JPEG bytes: the stored extension must follow the sniffed format, not any filename.
        var result = await env.Posts.CreateAsync(
            new MemoryStream(TestEnvironment.MakeJpeg(64, 64)), null, "", Ct);

        var created = Assert.IsType<PostCreateResult.Created>(result);
        Assert.Equal(".jpg", created.Post.FileExtension);
        Assert.Equal("image/jpeg", created.Post.MimeType);
    }

    [Fact]
    public async Task ListAsync_orders_newest_first_and_paginates()
    {
        using var env = new TestEnvironment();

        for (uint i = 1; i <= 5; i++)
        {
            // Distinct dimensions produce distinct bytes, so none of these deduplicate.
            await env.Posts.CreateAsync(new MemoryStream(TestEnvironment.MakePng(50 + i, 50)), null, "", Ct);
        }

        var (firstPage, total) = await env.Posts.ListAsync(null, page: 1, pageSize: 2, Ct);
        var (secondPage, _) = await env.Posts.ListAsync(null, page: 2, pageSize: 2, Ct);

        Assert.Equal(5, total);
        Assert.Equal(2, firstPage.Count);

        // Newest first: the last created post leads.
        Assert.Equal(5, firstPage[0].Id);
        Assert.Equal(4, firstPage[1].Id);
        Assert.Equal(3, secondPage[0].Id);
    }

    [Fact]
    public async Task DeleteAsync_removes_the_row_and_its_blobs()
    {
        using var env = new TestEnvironment();

        var created = Assert.IsType<PostCreateResult.Created>(
            await env.Posts.CreateAsync(new MemoryStream(TestEnvironment.MakePng(80, 80)), null, "", Ct));
        var sha = created.Post.Sha256;

        var deleted = await env.Posts.DeleteAsync(created.Post.Id, Ct);

        Assert.True(deleted);
        Assert.Equal(0, await env.Db.Posts.CountAsync(Ct));
        Assert.False(env.Store.OriginalExists(sha, ".png"));
        Assert.False(env.Store.ThumbExists(sha));
    }

    [Fact]
    public async Task DeleteAsync_returns_false_for_a_post_that_does_not_exist()
    {
        using var env = new TestEnvironment();

        Assert.False(await env.Posts.DeleteAsync(4242, Ct));
    }
}
