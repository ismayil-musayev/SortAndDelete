using System.Numerics;

namespace SortAndDelete.Models;

/// <summary>
/// Everything the duplicate scan knows about one image, derived once from a small
/// thumbnail and then cached.
///
/// Levels are *queries* over these fields rather than separate scans, so retuning a
/// threshold — or adding a whole new level — costs a re-grouping (milliseconds) instead
/// of decoding the library again.
/// </summary>
public sealed class ImageSignature
{
    /// <summary>Gradient-direction hash of the upright image.</summary>
    public required ulong DHash { get; init; }

    /// <summary>
    /// The same hash for the three quarter turns. Kept because it is 24 bytes and saves a
    /// full rescan the day rotated copies need to match.
    /// </summary>
    public required ulong DHash90 { get; init; }

    public required ulong DHash180 { get; init; }

    public required ulong DHash270 { get; init; }

    /// <summary>Coarse-structure hash. Fails differently from <see cref="DHash"/>, which is why both are stored.</summary>
    public required ulong PHash { get; init; }

    /// <summary>The coarse hash at each quarter turn, so a turned copy can be confirmed as well as found.</summary>
    public required ulong PHash90 { get; init; }

    public required ulong PHash180 { get; init; }

    public required ulong PHash270 { get; init; }

    /// <summary>
    /// 4x4x4 RGB histogram, each bin holding its share of the thumbnail scaled to 0-255.
    /// Separates images that share a layout but not their colours.
    /// </summary>
    public required byte[] Histogram { get; init; }

    /// <summary>
    /// Standard deviation of thumbnail brightness, 0-255. Flat, dark and blank-document
    /// images land near zero, and that is exactly where both hashes stop meaning anything —
    /// every low-detail image resembles every other one.
    /// </summary>
    public required double Detail { get; init; }

    /// <summary>Differing bits between the two upright gradient hashes.</summary>
    public int DistanceTo(ImageSignature other) => BitOperations.PopCount(DHash ^ other.DHash);

    /// <summary>Differing bits between the two coarse-structure hashes.</summary>
    public int CoarseDistanceTo(ImageSignature other) => BitOperations.PopCount(PHash ^ other.PHash);

    /// <summary>
    /// Both distances measured at whichever quarter turn of <paramref name="other"/> lines up
    /// best.
    /// </summary>
    /// <remarks>
    /// The pair comes from a single orientation on purpose. Taking each hash's best turn
    /// independently would let one vouch for the picture upright while the other vouched for
    /// it sideways, and two hashes describing different pictures is exactly the agreement
    /// this design relies on not happening.
    /// </remarks>
    public (int Gradient, int Coarse) BestAlignedDistances(ImageSignature other)
    {
        var best = (Gradient: BitOperations.PopCount(DHash ^ other.DHash),
                    Coarse: BitOperations.PopCount(PHash ^ other.PHash));

        Consider(other.DHash90, other.PHash90);
        Consider(other.DHash180, other.PHash180);
        Consider(other.DHash270, other.PHash270);

        return best;

        void Consider(ulong gradient, ulong coarse)
        {
            int distance = BitOperations.PopCount(DHash ^ gradient);
            if (distance < best.Gradient)
                best = (distance, BitOperations.PopCount(PHash ^ coarse));
        }
    }

    /// <summary>
    /// Histogram overlap, 0 (no shared colours) to 1 (identical distribution).
    /// </summary>
    public double HistogramOverlap(ImageSignature other)
    {
        if (Histogram.Length != other.Histogram.Length)
            return 0;

        int shared = 0;
        int total = 0;
        for (int i = 0; i < Histogram.Length; i++)
        {
            shared += Math.Min(Histogram[i], other.Histogram[i]);
            total += Math.Max(Histogram[i], other.Histogram[i]);
        }
        return total == 0 ? 1 : (double)shared / total;
    }
}
