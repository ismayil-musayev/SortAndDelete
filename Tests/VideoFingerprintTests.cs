using SortAndDelete.Logic;
using SortAndDelete.Models;
using Xunit;

namespace SortAndDelete.Tests;

public class VideoFingerprintTests
{
    private static ImageSignature Frame(double detail = 50) => new()
    {
        DHash = 0,
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
}
