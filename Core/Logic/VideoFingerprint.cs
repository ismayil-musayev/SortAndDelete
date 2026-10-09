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
    /// Reads a clip's frames through <paramref name="readFrame"/>, which gets a sample index and
    /// returns null when that frame can't be read. Every position is tried, in order, even after
    /// one that can't be read, and each result keeps its own slot: closing a gap up would pair
    /// every later frame with a different moment of the other clip. An exception ends the clip.
    /// </summary>
    public static Task<ImageSignature?[]> SampleAsync(Func<int, Task<ImageSignature?>> readFrame) =>
        SampleAsync(new ImageSignature?[SamplePositions.Length], readFrame);

    /// <summary>
    /// The same for a clip whose frames are partly known already: <paramref name="known"/> has a
    /// slot per sample position, null where that frame is still to be read. Only those positions
    /// are read, in order, and the known frames stay in their slots, so a clip missing one frame
    /// costs one read instead of all of them. <paramref name="known"/> itself is left as it is.
    /// </summary>
    public static async Task<ImageSignature?[]> SampleAsync(
        IReadOnlyList<ImageSignature?> known, Func<int, Task<ImageSignature?>> readFrame)
    {
        if (known.Count != SamplePositions.Length)
        {
            throw new ArgumentException(
                $"Expected a slot for each of the {SamplePositions.Length} sample positions, got {known.Count}.",
                nameof(known));
        }

        var frames = new ImageSignature?[SamplePositions.Length];
        for (int i = 0; i < frames.Length; i++)
            frames[i] = known[i] ?? await readFrame(i);
        return frames;
    }

    /// <summary>
    /// Whether two clips' frames agree, each paired with the frame taken at the same position
    /// in the other clip. Frames too flat to mean anything are set aside rather than counted
    /// either way — a fade to black in both clips is not evidence of sameness — and so are
    /// positions where either clip's frame couldn't be read.
    /// </summary>
    public static bool FramesAgree(
        IReadOnlyList<ImageSignature?> left,
        IReadOnlyList<ImageSignature?> right,
        Func<ImageSignature, ImageSignature, bool> frameMatches,
        double minDetail)
    {
        int comparable = 0;
        int agreed = 0;

        for (int i = 0; i < Math.Min(left.Count, right.Count); i++)
        {
            if (left[i] is not { } leftFrame || right[i] is not { } rightFrame)
                continue;

            if (leftFrame.Detail < minDetail || rightFrame.Detail < minDetail)
                continue;

            comparable++;
            if (frameMatches(leftFrame, rightFrame))
                agreed++;
        }

        return comparable >= MinimumComparableFrames
            && agreed >= (int)Math.Ceiling(comparable * RequiredAgreement);
    }
}
