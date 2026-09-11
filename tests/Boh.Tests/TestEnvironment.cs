using Boh.Web;
using Boh.Web.Data;
using Boh.Web.Services;
using ImageMagick;
using ImageMagick.Drawing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Boh.Tests;

/// <summary>
/// A throwaway data directory plus a real SQLite database. Tests run against the actual
/// provider rather than an in-memory substitute, because several behaviours under test
/// (unique index enforcement, integer timestamp ordering) only exist in real SQLite.
/// </summary>
public sealed class TestEnvironment : IDisposable
{
    public BohOptions Options { get; }
    public BohDbContext Db { get; }
    public ContentAddressedFileStore Store { get; }
    public PostService Posts { get; }
    public DuplicateService Duplicates { get; }
    public TagService Tags { get; }
    public UserService Users { get; }

    public TestEnvironment()
    {
        MagickMediaProcessor.ApplyResourceLimits();

        var root = Path.Combine(Path.GetTempPath(), "boh-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        Options = new BohOptions { DataPath = root };

        Db = new BohDbContext(new DbContextOptionsBuilder<BohDbContext>()
            .UseSqlite(Options.ConnectionString)
            .Options);
        Db.Database.Migrate();

        Store = new ContentAddressedFileStore(Options, NullLogger<ContentAddressedFileStore>.Instance);
        Store.EnsureDirectories();

        var processor = new MagickMediaProcessor(NullLogger<MagickMediaProcessor>.Instance);
        var registry = new MediaProcessorRegistry([processor]);

        Duplicates = new DuplicateService(
            Db, Store, registry, NullLogger<DuplicateService>.Instance);

        Posts = new PostService(
            Db,
            Store,
            registry,
            Duplicates,
            Options,
            NullLogger<PostService>.Instance);

        Tags = new TagService(Db, NullLogger<TagService>.Instance);
        Users = new UserService(Db, NullLogger<UserService>.Instance);
    }

    /// <summary>Creates a post with distinct content so it never deduplicates against another.</summary>
    public async Task<int> CreatePostAsync(uint size = 32)
    {
        var result = await Posts.CreateAsync(
            new MemoryStream(MakePng(size, size)), null, "", CancellationToken.None);

        return Assert.IsType<PostCreateResult.Created>(result).Post.Id;
    }

    public async Task<string[]> TagsOnAsync(int postId)
    {
        Db.ChangeTracker.Clear();
        var post = await Posts.GetAsync(postId, CancellationToken.None);
        return post!.PostTags
            .Select(pt => new Boh.Web.Tags.TagName(pt.Tag.Namespace, pt.Tag.Name).Display)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<Boh.Web.Data.Entities.TagSource?> SourceOfAsync(int postId, string display)
    {
        Db.ChangeTracker.Clear();
        var post = await Posts.GetAsync(postId, CancellationToken.None);
        var match = post!.PostTags.FirstOrDefault(
            pt => new Boh.Web.Tags.TagName(pt.Tag.Namespace, pt.Tag.Name).Display == display);

        return match?.Source;
    }

    /// <summary>Writes a real encoded image so decoding is exercised, not stubbed.</summary>
    public static byte[] MakePng(uint width, uint height, string color = "#4488cc")
    {
        using var image = new MagickImage(new MagickColor(color), width, height);
        image.Format = MagickFormat.Png;
        return image.ToByteArray();
    }

    public static byte[] MakeJpeg(uint width, uint height, string color = "#cc4444")
    {
        using var image = new MagickImage(new MagickColor(color), width, height);
        image.Format = MagickFormat.Jpeg;
        return image.ToByteArray();
    }

    /// <summary>
    /// A picture with structure to it: a fixed scatter of grey blocks, laid out in fractions
    /// of the canvas so the same <paramref name="seed"/> draws the same picture whatever size
    /// or format is asked for. That is what makes it usable for perceptual hashing, where the
    /// flat colours the other helpers produce have nothing to compare.
    /// </summary>
    public static byte[] MakePattern(
        uint width, uint height, MagickFormat format = MagickFormat.Png, int seed = 1)
    {
        using var image = new MagickImage(MagickColors.White, width, height);

        // Seeded, so a test can ask for "the same picture, bigger" or "the same picture as a
        // JPEG" and get exactly that.
        var random = new Random(seed);
        var drawables = new Drawables();

        for (var i = 0; i < 12; i++)
        {
            var x = random.NextDouble() * 0.75;
            var y = random.NextDouble() * 0.75;
            var w = 0.1 + random.NextDouble() * 0.25;
            var h = 0.1 + random.NextDouble() * 0.25;
            var shade = (byte)random.Next(0, 200);

            drawables
                .FillColor(new MagickColor(shade, shade, shade))
                .Rectangle(x * width, y * height, Math.Min(x + w, 1.0) * width, Math.Min(y + h, 1.0) * height);
        }

        drawables.Draw(image);

        image.Format = format;
        // Lossy enough to move a few pixels around, which is the point of hashing at all.
        if (format is MagickFormat.Jpeg) image.Quality = 70;

        return image.ToByteArray();
    }

    public void Dispose()
    {
        Db.Dispose();
        try
        {
            if (Directory.Exists(Options.DataPath)) Directory.Delete(Options.DataPath, recursive: true);
        }
        catch (IOException)
        {
            // A locked SQLite file on a slow CI agent is not worth failing a test over.
        }
    }
}
