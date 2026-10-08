using SortAndDelete.Models;

namespace SortAndDelete.Logic;

/// <summary>
/// How videos are compared.
///
/// Pulling frames out of a video costs hundreds of milliseconds, far too much to do across a
/// whole library. Duration rescues it: a re-encode keeps its running time to within about a
/// second, so a clip whose length nothing else shares cannot have a twin and never needs
/// decoding at all. Only the few that collide on length get fingerprinted.
/// </summary>
public static class VideoFingerprint
{
    /// <summary>
    /// Where frames are taken from, as fractions of the running time. The ends are avoided
    /// because clips so often open and close on black, and a black frame carries no structure
    /// to match on.
    /// </summary>
    public static readonly double[] SamplePositions = [0.20, 0.40, 0.60, 0.80];

    /// <summary>Running times within this of each other are treated as the same length.</summary>
    public static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(1);

    /// <summary>Frames that must be usable before a verdict is worth giving at all.</summary>
    public const int MinimumComparableFrames = 2;

    /// <summary>Share of the usable frames that has to agree.</summary>
    public const double RequiredAgreement = 0.67;

    /// <summary>
    /// The bucket a clip's duration falls in. Two clips can only match when their buckets are
    /// equal or adjacent, which is what keeps the comparison from being all-pairs.
    /// </summary>
    public static long DurationBucket(long durationMs) =>
        durationMs / (long)DurationTolerance.TotalMilliseconds;

    public static bool DurationsAgree(long left, long right) =>
        Math.Abs(left - right) <= (long)DurationTolerance.TotalMilliseconds;

    /// <summary>
    /// Whether two clips' frames agree. Frames too flat to mean anything are set aside rather
    /// than counted either way — a fade to black in both clips is not evidence of sameness.
    /// </summary>
    public static bool FramesAgree(
        IReadOnlyList<ImageSignature> left,
        IReadOnlyList<ImageSignature> right,
        Func<ImageSignature, ImageSignature, bool> frameMatches,
        double minDetail)
    {
        int comparable = 0;
        int agreed = 0;

        for (int i = 0; i < Math.Min(left.Count, right.Count); i++)
        {
            if (left[i].Detail < minDetail || right[i].Detail < minDetail)
                continue;

            comparable++;
            if (frameMatches(left[i], right[i]))
                agreed++;
        }

        return comparable >= MinimumComparableFrames
            && agreed >= (int)Math.Ceiling(comparable * RequiredAgreement);
    }
}
