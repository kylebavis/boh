using System.Numerics;

namespace Boh.Web.Media;

/// <summary>
/// A perceptual hash of an image: two pictures that look alike produce hashes differing in
/// only a few bits, which is what lets a resized or re-encoded repost be recognized once its
/// SHA-256 no longer matches anything.
/// </summary>
/// <remarks>
/// The construction is the usual DCT one — reduce the image to a small greyscale grid, take
/// the two-dimensional DCT-II, and keep the sign of each low-frequency coefficient relative
/// to their median. Only the low frequencies survive, so the hash tracks the broad structure
/// of the image and ignores resolution, JPEG ringing and mild colour shifts.
///
/// It is deliberately not invariant to rotation, mirroring or cropping: those produce an
/// unrelated hash. Calling them the same picture is a judgement this application does not
/// make, and the invariant versions of this hash pay for it with false positives.
/// </remarks>
public static class PerceptualHash
{
    /// <summary>
    /// Edge of the greyscale grid the transform runs over. Larger than the block that ends up
    /// in the hash on purpose: downscaling to exactly 8x8 would alias fine detail into the
    /// coefficients the hash is made of, where downscaling to 32x32 first averages it away.
    /// </summary>
    public const int GridEdge = 32;

    /// <summary>Edge of the retained low-frequency block. 8x8 less the DC term is 63 bits.</summary>
    private const int BlockEdge = 8;

    /// <summary>How many of the block's coefficients become hash bits — everything but DC.</summary>
    private const int Bits = BlockEdge * BlockEdge - 1;

    /// <summary>
    /// Amplitude below which the low frequencies are treated as carrying no signal at all.
    /// In pixel units on a 0-255 scale, so this is "every retained coefficient is smaller than
    /// one shade" — a flat colour, a blank scan, a solid placeholder.
    /// </summary>
    /// <remarks>
    /// Such an image has no structure to compare, and thresholding its coefficients would
    /// yield whatever rounding error produced: every featureless image would hash alike and be
    /// reported as a duplicate of every other. Refusing to hash them is the honest answer.
    /// </remarks>
    private const double FlatAmplitude = 1.0;

    /// <summary>
    /// Basis functions of the orthonormal DCT-II, as <c>Basis[frequency, position]</c>. Only
    /// the frequencies that reach the hash are needed, and the table is the same for every
    /// image, so it is built once.
    /// </summary>
    private static readonly double[,] Basis = BuildBasis();

    /// <summary>
    /// Hashes a <see cref="GridEdge"/>-square greyscale grid, row-major, one byte per pixel.
    /// Null when the image has no detail to hash — see <see cref="FlatAmplitude"/>.
    /// </summary>
    /// <remarks>
    /// The result is signed because that is what SQLite's INTEGER is, and round-tripping
    /// through it must not change the bits. It is a bag of 63 flags rather than a quantity:
    /// the sign carries no meaning and two hashes must only ever be compared with
    /// <see cref="Distance"/>.
    /// </remarks>
    public static long? TryCompute(ReadOnlySpan<byte> greyscale)
    {
        if (greyscale.Length != GridEdge * GridEdge)
        {
            throw new ArgumentException(
                $"Expected a {GridEdge}x{GridEdge} greyscale grid, got {greyscale.Length} bytes.",
                nameof(greyscale));
        }

        var coefficients = Transform(greyscale);

        // Comparing against the median rather than the mean is what makes the hash survive
        // re-encoding: a coefficient near the mean flips on the slightest change, whereas the
        // median guarantees half the bits sit on each side however the values are distributed.
        var sorted = (double[])coefficients.Clone();
        Array.Sort(sorted);
        var median = sorted[Bits / 2];

        // The largest deviation from the median, which is the signal the bits are made of.
        var amplitude = Math.Max(Math.Abs(sorted[0] - median), Math.Abs(sorted[^1] - median));
        if (amplitude < FlatAmplitude) return null;

        var hash = 0UL;
        for (var bit = 0; bit < Bits; bit++)
        {
            if (coefficients[bit] > median) hash |= 1UL << bit;
        }

        return unchecked((long)hash);
    }

    /// <summary>
    /// How many bits two hashes differ in — the Hamming distance, 0 for identical structure
    /// and 63 at most. This is the only meaningful comparison between two hashes.
    /// </summary>
    public static int Distance(long a, long b) => BitOperations.PopCount(unchecked((ulong)(a ^ b)));

    /// <summary>
    /// The low-frequency coefficients that become hash bits, in bit order, with the DC term
    /// dropped. DC is the average brightness of the whole grid: it dwarfs every other
    /// coefficient, and a bit for it would only ever say "brighter than its own median".
    /// </summary>
    /// <remarks>
    /// Separable rather than a direct 2-D sum. The transform is a product of two 1-D ones, so
    /// running it over rows and then over columns costs 32x8x32 + 8x8x32 multiply-adds where
    /// the four-deep loop costs 8x8x32x32 — the same answer for a tenth of the work.
    /// </remarks>
    private static double[] Transform(ReadOnlySpan<byte> greyscale)
    {
        // rows[y, u]: horizontal frequency u of row y.
        var rows = new double[GridEdge, BlockEdge];

        for (var y = 0; y < GridEdge; y++)
        {
            var row = greyscale.Slice(y * GridEdge, GridEdge);

            for (var u = 0; u < BlockEdge; u++)
            {
                var sum = 0.0;
                for (var x = 0; x < GridEdge; x++) sum += Basis[u, x] * row[x];
                rows[y, u] = sum;
            }
        }

        var coefficients = new double[Bits];
        var bit = 0;

        for (var v = 0; v < BlockEdge; v++)
        {
            for (var u = 0; u < BlockEdge; u++)
            {
                if (v == 0 && u == 0) continue;

                var sum = 0.0;
                for (var y = 0; y < GridEdge; y++) sum += Basis[v, y] * rows[y, u];
                coefficients[bit++] = sum;
            }
        }

        return coefficients;
    }

    /// <summary>
    /// The orthonormal scaling is what keeps <see cref="FlatAmplitude"/> expressible in pixel
    /// units: with it, a coefficient of 1.0 means an oscillation of about one shade.
    /// </summary>
    private static double[,] BuildBasis()
    {
        var basis = new double[BlockEdge, GridEdge];

        for (var k = 0; k < BlockEdge; k++)
        {
            var scale = Math.Sqrt((k == 0 ? 1.0 : 2.0) / GridEdge);

            for (var n = 0; n < GridEdge; n++)
            {
                basis[k, n] = scale * Math.Cos(Math.PI * (2 * n + 1) * k / (2.0 * GridEdge));
            }
        }

        return basis;
    }
}
