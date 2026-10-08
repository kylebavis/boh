using System.Numerics;

namespace Boh.Web.Media;

/// <summary>
/// DCT perceptual hash: similar-looking images differ in few bits. Not invariant to
/// rotation, mirroring or cropping.
/// </summary>
public static class PerceptualHash
{
    /// <summary>Grid edge. Larger than the hashed block so downscaling averages fine detail away.</summary>
    public const int GridEdge = 32;

    /// <summary>Edge of the retained low-frequency block. 8x8 less the DC term is 63 bits.</summary>
    private const int BlockEdge = 8;

    /// <summary>Every block coefficient but DC.</summary>
    private const int Bits = BlockEdge * BlockEdge - 1;

    /// <summary>Below one shade of amplitude the image is flat and is not hashed.</summary>
    private const double FlatAmplitude = 1.0;

    /// <summary>Orthonormal DCT-II basis, <c>Basis[frequency, position]</c>.</summary>
    private static readonly double[,] Basis = BuildBasis();

    /// <summary>
    /// Hashes a <see cref="GridEdge"/>-square grayscale grid. Null when flat. Signed only
    /// to round-trip through SQLite INTEGER.
    /// </summary>
    public static long? TryCompute(ReadOnlySpan<byte> grayscale)
    {
        if (grayscale.Length != GridEdge * GridEdge)
        {
            throw new ArgumentException(
                $"Expected a {GridEdge}x{GridEdge} grayscale grid, got {grayscale.Length} bytes.",
                nameof(grayscale));
        }

        var coefficients = Transform(grayscale);

        // Median rather than mean keeps bits stable under re-encoding.
        var sorted = (double[])coefficients.Clone();
        Array.Sort(sorted);
        var median = sorted[Bits / 2];

        var amplitude = Math.Max(Math.Abs(sorted[0] - median), Math.Abs(sorted[^1] - median));
        if (amplitude < FlatAmplitude) return null;

        var hash = 0UL;
        for (var bit = 0; bit < Bits; bit++)
        {
            if (coefficients[bit] > median) hash |= 1UL << bit;
        }

        return unchecked((long)hash);
    }

    /// <summary>Hamming distance, 0 to 63.</summary>
    public static int Distance(long a, long b) => BitOperations.PopCount(unchecked((ulong)(a ^ b)));

    /// <summary>Low-frequency coefficients in bit order, DC dropped. Separable: rows then columns.</summary>
    private static double[] Transform(ReadOnlySpan<byte> grayscale)
    {
        // rows[y, u]: horizontal frequency u of row y.
        var rows = new double[GridEdge, BlockEdge];

        for (var y = 0; y < GridEdge; y++)
        {
            var row = grayscale.Slice(y * GridEdge, GridEdge);

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

    /// <summary>Orthonormal, so coefficients read in pixel units.</summary>
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
