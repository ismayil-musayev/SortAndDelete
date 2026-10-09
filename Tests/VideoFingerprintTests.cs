using SortAndDelete.Logic;
using SortAndDelete.Models;
using Xunit;

namespace SortAndDelete.Tests;

public class VideoFingerprintTests
{
    private static ImageSignature Frame(double detail = 50, ulong hash = 0) => new()
    {
        DHash = hash,
        DHash90 = 0,
        DHash180 = 0,
        DHash270 = 0,
        PHash = 0,
        PHash90 = 0,
        PHash180 = 0,
        PHash270 = 0,
        Histogram = [.. Enumerable.Repeat((byte)4, 64)],
        Detail = detail,
    };

    private static bool Always(ImageSignature a, ImageSignature b) => true;

    private static bool Never(ImageSignature a, ImageSignature b) => false;

    /// <summary>Agrees only on the very same frame, so pairing the wrong two shows.</summary>
    private static bool SameHash(ImageSignature a, ImageSignature b) => a.DHash == b.DHash;

    [Fact]
    public void Frames_are_sampled_away_from_the_ends()
    {
        // Clips open and close on black far too often for the ends to be worth reading.
        Assert.All(VideoFingerprint.SamplePositions, position => Assert.InRange(position, 0.05, 0.95));
        Assert.NotEmpty(VideoFingerprint.SamplePositions);
    }

    [Fact]
    public void Running_times_within_a_second_count_as_the_same_length()
    {
        Assert.True(VideoFingerprint.DurationsAgree(30_000, 30_000));
        Assert.True(VideoFingerprint.DurationsAgree(30_000, 30_900));
        Assert.False(VideoFingerprint.DurationsAgree(30_000, 31_500));
    }

    [Fact]
    public void Clips_either_side_of_a_bucket_edge_still_meet()
    {
        // Bucketing alone would separate 1999ms from 2001ms, which is why each clip is
        // carried in the next bucket as well.
        long low = VideoFingerprint.DurationBucket(1_999);
        long high = VideoFingerprint.DurationBucket(2_001);

        Assert.NotEqual(low, high);
        Assert.Equal(low + 1, high);
        Assert.True(VideoFingerprint.DurationsAgree(1_999, 2_001));
    }

    [Fact]
    public void A_majority_of_frames_has_to_agree()
    {
        var frames = new[] { Frame(), Frame(), Frame(), Frame() };

        Assert.True(VideoFingerprint.FramesAgree(frames, frames, Always, minDetail: 12));
        Assert.False(VideoFingerprint.FramesAgree(frames, frames, Never, minDetail: 12));
    }

    /// <summary>
    /// Two clips that both fade to black would otherwise "agree" on a frame carrying no
    /// information at all.
    /// </summary>
    [Fact]
    public void Frames_too_flat_to_mean_anything_are_set_aside()
    {
        var mostlyBlank = new[] { Frame(detail: 0.1), Frame(detail: 0.1), Frame(detail: 0.1), Frame(50) };

        Assert.False(VideoFingerprint.FramesAgree(mostlyBlank, mostlyBlank, Always, minDetail: 12));
    }

    [Fact]
    public void A_verdict_needs_more_than_one_usable_frame()
    {
        var barelyUsable = new[] { Frame(50), Frame(detail: 0.1), Frame(detail: 0.1), Frame(detail: 0.1) };

        Assert.False(VideoFingerprint.FramesAgree(barelyUsable, barelyUsable, Always, minDetail: 12));
    }

    [Fact]
    public void One_disagreeing_frame_out_of_four_is_tolerated()
    {
        var frames = new[] { Frame(), Frame(), Frame(), Frame() };
        int call = 0;

        bool ThirdFrameDiffers(ImageSignature a, ImageSignature b) => ++call != 3;

        Assert.True(VideoFingerprint.FramesAgree(frames, frames, ThirdFrameDiffers, minDetail: 12));
    }

    [Fact]
    public void Two_disagreeing_frames_out_of_four_is_not()
    {
        var frames = new[] { Frame(), Frame(), Frame(), Frame() };
        int call = 0;

        bool TwoFramesDiffer(ImageSignature a, ImageSignature b) => ++call > 2;

        Assert.False(VideoFingerprint.FramesAgree(frames, frames, TwoFramesDiffer, minDetail: 12));
    }

    /// <summary>
    /// A re-encode whose 20% frame couldn't be read still has to line its other three up with
    /// the same moments of the original. Closing up the gap compared its 40, 60 and 80% frames
    /// with the original's 20, 40 and 60%, and the copy was missed.
    /// </summary>
    [Fact]
    public void A_missing_frame_does_not_shift_the_frames_after_it()
    {
        ImageSignature?[] original = [Frame(hash: 1), Frame(hash: 2), Frame(hash: 3), Frame(hash: 4)];
        ImageSignature?[] firstUnread = [null, Frame(hash: 2), Frame(hash: 3), Frame(hash: 4)];

        Assert.True(VideoFingerprint.FramesAgree(original, firstUnread, SameHash, minDetail: 12));
        Assert.True(VideoFingerprint.FramesAgree(firstUnread, original, SameHash, minDetail: 12));
    }

    [Fact]
    public void Frames_that_could_not_be_read_count_neither_way()
    {
        ImageSignature?[] whole = [Frame(hash: 1), Frame(hash: 2), Frame(hash: 3), Frame(hash: 4)];
        ImageSignature?[] halfRead = [Frame(hash: 1), Frame(hash: 2), null, null];
        ImageSignature?[] halfReadOneOff = [Frame(hash: 1), Frame(hash: 9), null, null];

        // Not against: the two frames read in both clips agree, which is a verdict on its own.
        Assert.True(VideoFingerprint.FramesAgree(halfRead, whole, SameHash, minDetail: 12));

        // Not for: one of those two disagrees, and the gaps don't make up the difference.
        Assert.False(VideoFingerprint.FramesAgree(halfReadOneOff, whole, SameHash, minDetail: 12));
    }

    /// <summary>
    /// Only positions read in both clips count. Two frames each is enough on its own, but not
    /// when the clips only have one position in common.
    /// </summary>
    [Fact]
    public void A_verdict_needs_more_than_one_frame_read_in_both_clips()
    {
        ImageSignature?[] firstOnly = [Frame(hash: 1), null, null, null];
        ImageSignature?[] early = [Frame(hash: 1), Frame(hash: 2), null, null];
        ImageSignature?[] late = [null, Frame(hash: 2), Frame(hash: 3), null];

        Assert.False(VideoFingerprint.FramesAgree(firstOnly, firstOnly, Always, minDetail: 12));
        Assert.False(VideoFingerprint.FramesAgree(early, late, Always, minDetail: 12));
    }

    /// <summary>
    /// The scanner's half of keeping frames in line: a frame that can't be read leaves its slot
    /// empty instead of letting the next one move up, and doesn't stop the rest being read.
    /// </summary>
    [Fact]
    public async Task An_unreadable_frame_keeps_its_slot_and_the_rest_are_still_read()
    {
        var read = new[] { Frame(hash: 1), Frame(hash: 2), Frame(hash: 3), Frame(hash: 4) };
        var asked = new List<int>();

        var frames = await VideoFingerprint.SampleAsync(index =>
        {
            asked.Add(index);
            return Task.FromResult<ImageSignature?>(index == 1 ? null : read[index]);
        });

        ImageSignature?[] expected = [read[0], null, read[2], read[3]];
        Assert.Equal(expected, frames);
        Assert.Equal([0, 1, 2, 3], asked);
    }
}
