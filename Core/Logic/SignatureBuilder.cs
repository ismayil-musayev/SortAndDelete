using SortAndDelete.Models;

namespace SortAndDelete.Logic;

/// <summary>
/// Turns a small RGB thumbnail into the <see cref="ImageSignature"/> that gets cached.
///
/// The platform layer supplies the pixels (Android <c>Bitmap</c>, iOS <c>PHImageManager</c>),
/// which keeps this project free of any imaging dependency and lets the unit tests and the
/// calibration harness run the exact same code the phone runs.
/// </summary>
public static class SignatureBuilder
{
    /// <summary>Side of the square thumbnail callers must supply.</summary>
    public const int GridSize = PerceptualHash.GridSize;

    /// <summary>Bytes of tightly-packed RGB888 expected by <see cref="Build"/>.</summary>
    public const int ExpectedLength = GridSize * GridSize * 3;

    /// <summary>Bins per channel in the colour histogram; 4x4x4 gives 64 bins.</summary>
    private const int BinsPerChannel = 4;

    public static ImageSignature Build(ReadOnlySpan<byte> rgb)
    {
        if (rgb.Length != ExpectedLength)
            throw new ArgumentException($"Expected {ExpectedLength} bytes of {GridSize}x{GridSize} RGB, got {rgb.Length}.", nameof(rgb));

        Span<byte> gray = stackalloc byte[GridSize * GridSize];
        long sum = 0;
        long sumSquares = 0;
        for (int i = 0; i < gray.Length; i++)
        {
            // Rec. 601 luma — the same weighting the eye applies, so "detail" below
            // tracks visible contrast rather than raw channel spread.
            int value = ((rgb[i * 3] * 299) + (rgb[(i * 3) + 1] * 587) + (rgb[(i * 3) + 2] * 114)) / 1000;
            gray[i] = (byte)value;
            sum += value;
            sumSquares += (long)value * value;
        }

        double mean = (double)sum / gray.Length;
        double variance = ((double)sumSquares / gray.Length) - (mean * mean);

        Span<byte> rotated = stackalloc byte[GridSize * GridSize];
        Span<byte> scratch = stackalloc byte[GridSize * GridSize];

        // Both hashes are taken at each quarter turn, so a rotated copy can be confirmed by
        // the same orientation that found it.
        PerceptualHash.RotateQuarterTurn(gray, rotated);
        ulong dHash90 = PerceptualHash.Difference(rotated);
        ulong pHash90 = PerceptualHash.Dct(rotated);

        PerceptualHash.RotateQuarterTurn(rotated, scratch);
        ulong dHash180 = PerceptualHash.Difference(scratch);
        ulong pHash180 = PerceptualHash.Dct(scratch);

        PerceptualHash.RotateQuarterTurn(scratch, rotated);
        ulong dHash270 = PerceptualHash.Difference(rotated);
        ulong pHash270 = PerceptualHash.Dct(rotated);

        return new ImageSignature
        {
            DHash = PerceptualHash.Difference(gray),
            DHash90 = dHash90,
            DHash180 = dHash180,
            DHash270 = dHash270,
            PHash = PerceptualHash.Dct(gray),
            PHash90 = pHash90,
            PHash180 = pHash180,
            PHash270 = pHash270,
            Histogram = BuildHistogram(rgb),
            Detail = Math.Sqrt(Math.Max(0, variance)),
        };
    }

    private static byte[] BuildHistogram(ReadOnlySpan<byte> rgb)
    {
        const int shift = 8 - 2; // 256 levels down to BinsPerChannel
        int pixels = GridSize * GridSize;

        Span<int> counts = stackalloc int[BinsPerChannel * BinsPerChannel * BinsPerChannel];
        for (int i = 0; i < pixels; i++)
        {
            int r = rgb[i * 3] >> shift;
            int g = rgb[(i * 3) + 1] >> shift;
            int b = rgb[(i * 3) + 2] >> shift;
            counts[(((r * BinsPerChannel) + g) * BinsPerChannel) + b]++;
        }

        var histogram = new byte[counts.Length];
        for (int i = 0; i < counts.Length; i++)
            histogram[i] = (byte)(counts[i] * 255 / pixels);
        return histogram;
    }
}
