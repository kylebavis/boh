using Boh.Web.Services;
using ImageMagick;
using Microsoft.Extensions.Logging.Abstractions;
using PerceptualHash = Boh.Web.Media.PerceptualHash;

namespace Boh.Tests;

/// <summary>
/// The hash itself, exercised through the processor that produces it so real encoding and
/// decoding are part of every case. A perceptual hash is only worth anything if it survives
/// the transformations a repost actually goes through, and none of those can be faked.
/// </summary>
/// <remarks>
/// Aliased <c>using</c> at the top of the file because ImageMagick exports a type of the same
/// name — a different construction, and not the one under test.
/// </remarks>
public class PerceptualHashTests : IDisposable
{
    private static CancellationToken Ct => CancellationToken.None;

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "boh-hash-tests", Guid.NewGuid().ToString("N"));

    private readonly MagickMediaProcessor _processor = new(NullLogger<MagickMediaProcessor>.Instance);

    public PerceptualHashTests()
    {
        MagickMediaProcessor.ApplyResourceLimits();
        Directory.CreateDirectory(_dir);
    }

    /// <summary>Writes bytes where a decoder can reach them and hashes the result.</summary>
    private async Task<long?> HashAsync(byte[] bytes, string name)
    {
        var path = Path.Combine(_dir, name);
        await File.WriteAllBytesAsync(path, bytes, Ct);
        return await _processor.TryComputePerceptualHashAsync(path, Ct);
    }

    /// <summary>
    /// The case the whole feature exists for: the same picture, downscaled and re-encoded,
    /// shares no bytes with the original and so cannot be caught by its SHA-256.
    /// </summary>
    [Fact]
    public async Task The_same_picture_resized_and_re_encoded_hashes_almost_identically()
    {
        var original = await HashAsync(TestEnvironment.MakePattern(400, 400), "original.png");
        var repost = await HashAsync(
            TestEnvironment.MakePattern(160, 160, MagickFormat.Jpeg), "repost.jpg");

        Assert.NotNull(original);
        Assert.NotNull(repost);

        var distance = PerceptualHash.Distance(original.Value, repost.Value);
        Assert.True(distance <= DuplicateService.MaxDistance,
            $"a 400px PNG and its 160px JPEG re-encode were {distance} bits apart");
    }

    [Fact]
    public async Task Unrelated_pictures_hash_far_apart()
    {
        var first = await HashAsync(TestEnvironment.MakePattern(300, 300, seed: 1), "first.png");
        var second = await HashAsync(TestEnvironment.MakePattern(300, 300, seed: 2), "second.png");

        var distance = PerceptualHash.Distance(first!.Value, second!.Value);
        Assert.True(distance > DuplicateService.MaxDistance,
            $"two unrelated pictures were only {distance} bits apart");
    }

    /// <summary>
    /// Flattening onto a fixed background is what makes this hold. Without it the hash would
    /// describe whatever the decoder happened to leave under the alpha, and a picture would
    /// stop matching the version of itself that had the transparency flattened away.
    /// </summary>
    [Fact]
    public async Task Making_the_background_transparent_still_hashes_as_the_same_picture()
    {
        var opaque = await HashAsync(TestEnvironment.MakePattern(300, 300), "opaque.png");

        // The same picture with the white background made transparent instead.
        using var image = new MagickImage(await File.ReadAllBytesAsync(Path.Combine(_dir, "opaque.png"), Ct));
        image.ColorFuzz = new Percentage(5);
        image.Transparent(MagickColors.White);
        image.Format = MagickFormat.Png32;

        var transparent = await HashAsync(image.ToByteArray(), "transparent.png");

        Assert.NotNull(transparent);

        // Not bit-for-bit: making white transparent within a 5% fuzz also nudges the pixels
        // that were nearly white. What matters is that it stays well inside duplicate range.
        var distance = PerceptualHash.Distance(opaque!.Value, transparent.Value);
        Assert.True(distance <= DuplicateService.MaxDistance,
            $"flattening the alpha moved the hash {distance} bits");
    }

    /// <summary>
    /// An image with no structure has nothing to compare. If it hashed anyway, every blank
    /// scan and solid placeholder in the collection would report every other as a duplicate.
    /// </summary>
    [Fact]
    public async Task An_image_with_no_detail_is_not_hashed()
    {
        Assert.Null(await HashAsync(TestEnvironment.MakePng(300, 300), "flat.png"));
        Assert.Null(await HashAsync(TestEnvironment.MakeJpeg(300, 300), "flat.jpg"));
    }

    [Fact]
    public async Task A_file_that_does_not_decode_is_reported_as_no_hash()
    {
        var path = Path.Combine(_dir, "prose.png");
        await File.WriteAllTextAsync(path, "this is not an image, it is prose", Ct);

        Assert.Null(await _processor.TryComputePerceptualHashAsync(path, Ct));
    }

    [Fact]
    public void Distance_counts_differing_bits()
    {
        Assert.Equal(0, PerceptualHash.Distance(0, 0));
        Assert.Equal(0, PerceptualHash.Distance(-1, -1));
        Assert.Equal(1, PerceptualHash.Distance(0, 1));
        Assert.Equal(3, PerceptualHash.Distance(0b1011, 0));

        // The sign bit is a bit like any other; a hash is not a number.
        Assert.Equal(64, PerceptualHash.Distance(0, -1));
        Assert.Equal(
            PerceptualHash.Distance(long.MinValue, 0),
            PerceptualHash.Distance(0, long.MinValue));
    }

    [Fact]
    public void A_grid_of_the_wrong_size_is_a_programming_error()
    {
        Assert.Throws<ArgumentException>(() => PerceptualHash.TryCompute(new byte[16]));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is not worth failing a test over.
        }
    }
}
