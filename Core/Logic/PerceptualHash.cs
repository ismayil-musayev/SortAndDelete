using System.Numerics;

namespace SortAndDelete.Logic;

/// <summary>
/// Resolution- and compression-independent image fingerprints.
///
/// Both hashes read a small grayscale grid, so a 4000px original and the 1600px copy
/// WhatsApp makes of it collapse onto nearly the same bits. That is the whole point:
/// the two files share no bytes, so a checksum sees unrelated data.
///
/// The two hashes fail in different ways on purpose — dHash tracks local gradients and
/// pHash coarse structure — so requiring both to agree rules out matches that only one
/// of them believes in.
/// </summary>
public static class PerceptualHash
{
    /// <summary>Side of the square grayscale grid every method here expects.</summary>
    public const int GridSize = 32;

    private const int DctSize = 8;

    /// <summary>cos[(2x+1)·u·π / 2N] for every grid position x and coefficient u.</summary>
    private static readonly float[] Cosines = BuildCosines();

    private static float[] BuildCosines()
    {
        var table = new float[GridSize * DctSize];
        for (int x = 0; x < GridSize; x++)
        {
            for (int u = 0; u < DctSize; u++)
                table[(x * DctSize) + u] = (float)Math.Cos((((2 * x) + 1) * u * Math.PI) / (2 * GridSize));
        }
        return table;
    }

    /// <summary>
    /// Difference hash: one bit per horizontal neighbour pair on a 9x8 grid, set when
    /// brightness increases to the right. Encodes gradient *direction*, which survives
    /// rescaling and recompression but moves as soon as the picture content changes.
    /// </summary>
    public static ulong Difference(ReadOnlySpan<byte> gray)
    {
        Require(gray);

        Span<int> cells = stackalloc int[9 * 8];
        Downsample(gray, cells, 9, 8);

        ulong hash = 0;
        int bit = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++, bit++)
            {
                if (cells[(y * 9) + x] < cells[(y * 9) + x + 1])
                    hash |= 1UL << bit;
            }
        }
        return hash;
    }

    /// <summary>
    /// Perceptual hash: the sign of the 64 lowest-frequency DCT coefficients relative to
    /// their median. Keeping only coarse structure makes it tolerate heavier compression
    /// than <see cref="Difference"/>.
    /// </summary>
    /// <remarks>
    /// The DC coefficient is left in the word but excluded from the median, so its bit is
    /// effectively constant and contributes nothing to any distance. That costs one bit of
    /// the 64 and keeps the layout obvious.
    /// </remarks>
    public static ulong Dct(ReadOnlySpan<byte> gray)
    {
        Require(gray);

        // Separable: rows first, then columns. Evaluating the same 64 coefficients
        // directly would cost 64x1024 multiply-adds; this way it is about 10k.
        Span<float> rows = stackalloc float[GridSize * DctSize];
        for (int y = 0; y < GridSize; y++)
        {
            for (int u = 0; u < DctSize; u++)
            {
                float sum = 0;
                for (int x = 0; x < GridSize; x++)
                    sum += gray[(y * GridSize) + x] * Cosines[(x * DctSize) + u];
                rows[(y * DctSize) + u] = sum;
            }
        }

        Span<float> coefficients = stackalloc float[DctSize * DctSize];
        for (int v = 0; v < DctSize; v++)
        {
            for (int u = 0; u < DctSize; u++)
            {
                float sum = 0;
                for (int y = 0; y < GridSize; y++)
                    sum += rows[(y * DctSize) + u] * Cosines[(y * DctSize) + v];
                coefficients[(v * DctSize) + u] = sum;
            }
        }

        float median = MedianExcludingDc(coefficients);

        ulong hash = 0;
        for (int i = 0; i < coefficients.Length; i++)
        {
            if (coefficients[i] > median)
                hash |= 1UL << i;
        }
        return hash;
    }

    /// <summary>Number of differing bits — 0 is identical, 32 is the average for unrelated images.</summary>
    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    /// <summary>
    /// The grid turned a quarter turn clockwise. Used to match copies that were rotated
    /// (or that carry a different EXIF orientation) without decoding the image again.
    /// </summary>
    public static void RotateQuarterTurn(ReadOnlySpan<byte> gray, Span<byte> target)
    {
        Require(gray);
        Require(target);

        for (int y = 0; y < GridSize; y++)
        {
            for (int x = 0; x < GridSize; x++)
                target[(x * GridSize) + (GridSize - 1 - y)] = gray[(y * GridSize) + x];
        }
    }

    /// <summary>Area-averages the grid down to <paramref name="width"/> x <paramref name="height"/>.</summary>
    private static void Downsample(ReadOnlySpan<byte> gray, Span<int> target, int width, int height)
    {
        for (int oy = 0; oy < height; oy++)
        {
            int y0 = oy * GridSize / height;
            int y1 = (oy + 1) * GridSize / height;
            for (int ox = 0; ox < width; ox++)
            {
                int x0 = ox * GridSize / width;
                int x1 = (ox + 1) * GridSize / width;

                int sum = 0;
                int count = 0;
                for (int y = y0; y < y1; y++)
                {
                    for (int x = x0; x < x1; x++)
                    {
                        sum += gray[(y * GridSize) + x];
                        count++;
                    }
                }
                target[(oy * width) + ox] = count == 0 ? 0 : sum / count;
            }
        }
    }

    private static float MedianExcludingDc(ReadOnlySpan<float> coefficients)
    {
        Span<float> sorted = stackalloc float[coefficients.Length - 1];
        coefficients[1..].CopyTo(sorted);
        sorted.Sort();
        return sorted[sorted.Length / 2];
    }

    private static void Require(ReadOnlySpan<byte> gray)
    {
        if (gray.Length != GridSize * GridSize)
            throw new ArgumentException($"Expected a {GridSize}x{GridSize} grayscale grid, got {gray.Length} bytes.", nameof(gray));
    }
}
