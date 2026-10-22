using System.Text;
using Boh.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Boh.Tests;

public class PostServiceTests
{
    private static CancellationToken Ct => TestContext.Current!.Execution.CancellationToken;

    [Test]
    public async Task CreateAsync_stores_the_post_and_generates_a_thumbnail()
    {
        using var env = new TestEnvironment();

        var result = await env.Posts.CreateAsync(
            new MemoryStream(TestEnvironment.MakePng(300, 200)), null, "", Ct);

        var created = (await Assert.That(result).IsTypeOf<PostCreateResult.Created>())!;
        await Assert.That(created.Post.Width).IsEqualTo(300);
        await Assert.That(created.Post.Height).IsEqualTo(200);
        await Assert.That(created.Post.MimeType).IsEqualTo("image/png");
        await Assert.That(created.Post.FileExtension).IsEqualTo(".png");
        await Assert.That(created.Post.IsVideo).IsFalse();

        await Assert.That(env.Store.OriginalExists(created.Post.Sha256, ".png")).IsTrue();
        await Assert.That(env.Store.ThumbExists(created.Post.Sha256)).IsTrue();
    }

    [Test]
    public async Task CreateAsync_reports_a_duplicate_instead_of_storing_twice()
    {
        using var env = new TestEnvironment();
        var bytes = TestEnvironment.MakePng(120, 120);

        var first = (await Assert.That(await env.Posts.CreateAsync(new MemoryStream(bytes), null, "", Ct)).IsTypeOf<PostCreateResult.Created>())!;

        var second = await env.Posts.CreateAsync(new MemoryStream(bytes), null, "", Ct);

        var duplicate = (await Assert.That(second).IsTypeOf<PostCreateResult.Duplicate>())!;
        await Assert.That(duplicate.ExistingPostId).IsEqualTo(first.Post.Id);
        await Assert.That(await env.Db.Posts.CountAsync(Ct)).IsEqualTo(1);
    }

    [Test]
    public async Task CreateAsync_rejects_content_that_is_not_an_image()
    {
        using var env = new TestEnvironment();
        var bytes = Encoding.UTF8.GetBytes("this is not an image, it is prose");

        var result = await env.Posts.CreateAsync(new MemoryStream(bytes), null, "", Ct);

        await Assert.That(result).IsTypeOf<PostCreateResult.Rejected>();
        await Assert.That(await env.Db.Posts.CountAsync(Ct)).IsEqualTo(0);
    }

    [Test]
    public async Task CreateAsync_rejects_an_empty_file()
    {
        using var env = new TestEnvironment();

        var result = await env.Posts.CreateAsync(new MemoryStream([]), null, "", Ct);

        await Assert.That(result).IsTypeOf<PostCreateResult.Rejected>();
    }

    [Test]
    public async Task CreateAsync_trusts_content_over_the_declared_format()
    {
        using var env = new TestEnvironment();

        // JPEG bytes: the stored extension must follow the sniffed format, not any filename.
        var result = await env.Posts.CreateAsync(
            new MemoryStream(TestEnvironment.MakeJpeg(64, 64)), null, "", Ct);

        var created = (await Assert.That(result).IsTypeOf<PostCreateResult.Created>())!;
        await Assert.That(created.Post.FileExtension).IsEqualTo(".jpg");
        await Assert.That(created.Post.MimeType).IsEqualTo("image/jpeg");
    }

    [Test]
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

        await Assert.That(total).IsEqualTo(5);
        await Assert.That(firstPage.Count).IsEqualTo(2);

        // Newest first: the last created post leads.
        await Assert.That(firstPage[0].Id).IsEqualTo(5);
        await Assert.That(firstPage[1].Id).IsEqualTo(4);
        await Assert.That(secondPage[0].Id).IsEqualTo(3);
    }

    [Test]
    public async Task DeleteAsync_removes_the_row_and_its_blobs()
    {
        using var env = new TestEnvironment();

        var created = (await Assert.That(await env.Posts.CreateAsync(new MemoryStream(TestEnvironment.MakePng(80, 80)), null, "", Ct)).IsTypeOf<PostCreateResult.Created>())!;
        var sha = created.Post.Sha256;

        var deleted = await env.Posts.DeleteAsync(created.Post.Id, Ct);

        await Assert.That(deleted).IsTrue();
        await Assert.That(await env.Db.Posts.CountAsync(Ct)).IsEqualTo(0);
        await Assert.That(env.Store.OriginalExists(sha, ".png")).IsFalse();
        await Assert.That(env.Store.ThumbExists(sha)).IsFalse();
    }

    [Test]
    public async Task DeleteAsync_returns_false_for_a_post_that_does_not_exist()
    {
        using var env = new TestEnvironment();

        await Assert.That(await env.Posts.DeleteAsync(4242, Ct)).IsFalse();
    }
}
