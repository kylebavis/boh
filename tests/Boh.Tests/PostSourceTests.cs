using Boh.Web;
using Boh.Web.Data;
using Boh.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Boh.Tests;

/// <summary>
/// A post can be reachable from several addresses. These cover the two halves of that:
/// recording an address without ever recording it twice, and keeping the addresses that
/// existed before the column became a table.
/// </summary>
public class PostSourceTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private const string Booru = "https://example.invalid/posts/1";
    private const string Mirror = "https://elsewhere.invalid/art/42";

    private static async Task<string[]> SourcesOnAsync(TestEnvironment env, int postId)
    {
        env.Db.ChangeTracker.Clear();
        var post = await env.Posts.GetAsync(postId, Ct);
        return post!.Sources.Select(s => s.Url).ToArray();
    }

    [Fact]
    public async Task CreateAsync_records_the_url_the_file_arrived_from()
    {
        using var env = new TestEnvironment();

        var result = await env.Posts.CreateAsync(
            new MemoryStream(TestEnvironment.MakePng(64, 64)), null, Booru, Ct);

        var created = Assert.IsType<PostCreateResult.Created>(result);
        Assert.Equal([Booru], await SourcesOnAsync(env, created.Post.Id));
    }

    [Fact]
    public async Task A_direct_upload_records_no_source()
    {
        using var env = new TestEnvironment();

        var id = await env.CreatePostAsync();

        Assert.Empty(await SourcesOnAsync(env, id));
    }

    /// <summary>
    /// The case the single column could not express: the same file posted to two sites. The
    /// second import is still not a new post, but the address it came from is not lost.
    /// </summary>
    [Fact]
    public async Task The_same_file_found_at_another_url_records_both_addresses()
    {
        using var env = new TestEnvironment();
        var bytes = TestEnvironment.MakePng(96, 96);

        var first = Assert.IsType<PostCreateResult.Created>(
            await env.Posts.CreateAsync(new MemoryStream(bytes), null, Booru, Ct));

        var second = await env.Posts.CreateAsync(new MemoryStream(bytes), null, Mirror, Ct);

        var duplicate = Assert.IsType<PostCreateResult.Duplicate>(second);
        Assert.Equal(first.Post.Id, duplicate.ExistingPostId);
        Assert.True(duplicate.SourceAdded);

        Assert.Equal(1, await env.Db.Posts.CountAsync(Ct));

        // Recorded order, which is what the detail page renders.
        Assert.Equal([Booru, Mirror], await SourcesOnAsync(env, first.Post.Id));
    }

    [Fact]
    public async Task Re_importing_the_same_url_does_not_record_it_twice()
    {
        using var env = new TestEnvironment();
        var bytes = TestEnvironment.MakePng(97, 97);

        var first = Assert.IsType<PostCreateResult.Created>(
            await env.Posts.CreateAsync(new MemoryStream(bytes), null, Booru, Ct));

        var second = await env.Posts.CreateAsync(new MemoryStream(bytes), null, Booru, Ct);

        var duplicate = Assert.IsType<PostCreateResult.Duplicate>(second);
        Assert.False(duplicate.SourceAdded);
        Assert.Equal([Booru], await SourcesOnAsync(env, first.Post.Id));
    }

    [Fact]
    public async Task Uploading_a_file_that_already_exists_leaves_its_sources_alone()
    {
        using var env = new TestEnvironment();
        var bytes = TestEnvironment.MakePng(98, 98);

        var first = Assert.IsType<PostCreateResult.Created>(
            await env.Posts.CreateAsync(new MemoryStream(bytes), null, Booru, Ct));

        // An upload carries no URL, so there is nothing to record.
        var second = await env.Posts.CreateAsync(new MemoryStream(bytes), null, "", Ct);

        Assert.False(Assert.IsType<PostCreateResult.Duplicate>(second).SourceAdded);
        Assert.Equal([Booru], await SourcesOnAsync(env, first.Post.Id));
    }

    [Fact]
    public async Task AddSourceAsync_ignores_blank_input_and_repeats()
    {
        using var env = new TestEnvironment();
        var id = await env.CreatePostAsync();

        Assert.False(await env.Posts.AddSourceAsync(id, null, Ct));
        Assert.False(await env.Posts.AddSourceAsync(id, "   ", Ct));

        // Not an address anyone could follow. The service refuses rather than trusting its
        // callers, so no entry point can put junk in the table.
        Assert.False(await env.Posts.AddSourceAsync(id, "not a url", Ct));
        Assert.False(await env.Posts.AddSourceAsync(id, "/posts/1", Ct));
        Assert.False(await env.Posts.AddSourceAsync(id, "javascript:alert(1)", Ct));

        Assert.True(await env.Posts.AddSourceAsync(id, Booru, Ct));

        // Surrounding whitespace is not a different address.
        Assert.False(await env.Posts.AddSourceAsync(id, $"  {Booru}  ", Ct));

        Assert.Equal([Booru], await SourcesOnAsync(env, id));
    }

    /// <summary>
    /// The migration creates the table and fills it before dropping the column; scaffolded in
    /// the other order it would have silently emptied the source of every existing post.
    /// </summary>
    [Fact]
    public async Task The_migration_keeps_the_source_of_a_post_that_predates_the_table()
    {
        var root = Path.Combine(Path.GetTempPath(), "boh-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var options = new BohOptions { DataPath = root };
            await using var db = new BohDbContext(new DbContextOptionsBuilder<BohDbContext>()
                .UseSqlite(options.ConnectionString)
                .Options);

            // The schema as it stood before sources moved out of the Posts row.
            await db.GetService<IMigrator>().MigrateAsync("TagNamespaceAliases", Ct);

            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO Posts
                    (Sha256, FileExtension, MimeType, FileSizeBytes, Width, Height, IsVideo,
                     SourceUrl, Description, UploadedAt)
                VALUES
                    ('a1', '.png', 'image/png', 10, 4, 4, 0, {0}, '', 0),
                    ('b2', '.png', 'image/png', 10, 4, 4, 0, '', '', 0)
                """,
                [Booru], Ct);

            await db.Database.MigrateAsync(Ct);

            var sources = await db.PostSources.AsNoTracking()
                .Join(db.Posts, s => s.PostId, p => p.Id, (s, p) => new { p.Sha256, s.Url })
                .ToListAsync(Ct);

            // The post that had one keeps it; the one with an empty column gets no row rather
            // than a row holding an empty string.
            var only = Assert.Single(sources);
            Assert.Equal("a1", only.Sha256);
            Assert.Equal(Booru, only.Url);

            // And back down: a single column cannot hold a list, so the first source returns to
            // it and the rest are lost. Rolling back should still leave a usable database.
            await db.GetService<IMigrator>().MigrateAsync("TagNamespaceAliases", Ct);

            var restored = await db.Database
                .SqlQueryRaw<string>("SELECT SourceUrl AS Value FROM Posts ORDER BY Id")
                .ToListAsync(Ct);

            Assert.Equal([Booru, ""], restored);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A locked SQLite file is not worth failing a test over.
            }
        }
    }

    [Fact]
    public async Task The_detail_page_lists_every_source()
    {
        using var app = new TestApp();
        var bytes = TestEnvironment.MakePng(72, 72);

        int postId;
        using (var scope = app.Services.CreateScope())
        {
            var posts = scope.ServiceProvider.GetRequiredService<PostService>();

            postId = Assert.IsType<PostCreateResult.Created>(
                await posts.CreateAsync(new MemoryStream(bytes), null, Booru, Ct)).Post.Id;

            Assert.True(await posts.AddSourceAsync(postId, Mirror, Ct));
        }

        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), $"/Posts/Detail/{postId}");

        Assert.Contains("<h2>Sources</h2>", html);
        Assert.Contains($"href=\"{Booru}\"", html);
        Assert.Contains($"href=\"{Mirror}\"", html);
    }
}
