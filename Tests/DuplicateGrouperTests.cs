using SortAndDelete.Logic;
using SortAndDelete.Models;
using Xunit;

namespace SortAndDelete.Tests;

public class DuplicateGrouperTests
{
    /// <summary>Every bin equally filled, so histogram overlap never gets in the way of a test.</summary>
    private static byte[] EvenColours() => [.. Enumerable.Repeat((byte)4, 64)];

    private static ImageSignature Signature(ulong hash, double detail = 50, byte[]? histogram = null) =>
        Turning(hash, hash, hash, hash, detail, histogram);

    /// <summary>
    /// A signature whose four quarter turns hash to the given values, so a turned copy can be
    /// modelled as the same list rotated by one.
    /// </summary>
    private static ImageSignature Turning(
        ulong upright, ulong quarter, ulong half, ulong threeQuarter,
        double detail = 50, byte[]? histogram = null) => new()
    {
        DHash = upright,
        DHash90 = quarter,
        DHash180 = half,
        DHash270 = threeQuarter,
        PHash = upright,
        PHash90 = quarter,
        PHash180 = half,
        PHash270 = threeQuarter,
        Histogram = histogram ?? EvenColours(),
        Detail = detail,
    };

    private static PhotoAsset Photo(
        string id,
        long size = 1_000_000,
        int width = 4000,
        int height = 3000,
        string takenAt = "2026-07-01T12:00:00",
        string folder = "DCIM/Camera/",
        MediaKind kind = MediaKind.Image,
        long durationMs = 0,
        bool edited = false) => new()
    {
        Id = id,
        SizeBytes = size,
        Width = width,
        Height = height,
        TakenAt = DateTime.Parse(takenAt),
        FolderPath = folder,
        FileName = $"{id}.jpg",
        Kind = kind,
        DurationMs = durationMs,
        IsEdited = edited,
    };

    private static ScannedPhoto Scanned(PhotoAsset photo, ImageSignature? signature = null, string? contentHash = null) =>
        new() { Photo = photo, Signature = signature, ContentHash = contentHash };

    private static ScannedPhoto ScannedClip(PhotoAsset photo, params ImageSignature[] frames) =>
        new() { Photo = photo, Frames = frames };

    private static PhotoAsset Clip(string id, long durationMs, long size = 50_000_000, string takenAt = "2026-07-01T12:00:00") =>
        Photo(id, size: size, takenAt: takenAt, kind: MediaKind.Video, durationMs: durationMs);

    // ---------- The exact tier ----------

    [Theory]
    [InlineData(DuplicateLevel.Exact)]
    [InlineData(DuplicateLevel.SamePhoto)]
    [InlineData(DuplicateLevel.Adjusted)]
    [InlineData(DuplicateLevel.SameMoment)]
    [InlineData(DuplicateLevel.Similar)]
    public void Byte_identical_files_group_at_every_level(DuplicateLevel level)
    {
        List<ScannedPhoto> items =
        [
            Scanned(Photo("original"), contentHash: "abc"),
            Scanned(Photo("copy"), contentHash: "abc"),
        ];

        var group = Assert.Single(DuplicateGrouper.Group(items, level));

        Assert.Equal(2, group.Items.Count);
        Assert.True(group.IsExact);
    }

    [Fact]
    public void Exact_level_ignores_how_alike_two_pictures_look()
    {
        List<ScannedPhoto> items =
        [
            Scanned(Photo("a"), Signature(0b0000), contentHash: "one"),
            Scanned(Photo("b"), Signature(0b0011), contentHash: "two"),
        ];

        Assert.Empty(DuplicateGrouper.Group(items, DuplicateLevel.Exact));
    }

    /// <summary>
    /// An iOS edit leaves the original's bytes alone, so the edited photo can still carry the
    /// hash it had as a byte-identical copy. It no longer looks like those bytes.
    /// </summary>
    [Theory]
    [InlineData(DuplicateLevel.Exact)]
    [InlineData(DuplicateLevel.SamePhoto)]
    [InlineData(DuplicateLevel.Adjusted)]
    [InlineData(DuplicateLevel.SameMoment)]
    [InlineData(DuplicateLevel.Similar)]
    public void An_edited_photo_is_never_an_identical_file(DuplicateLevel level)
    {
        List<ScannedPhoto> items =
        [
            Scanned(Photo("original"), contentHash: "abc"),
            Scanned(Photo("edited", edited: true), contentHash: "abc"),
        ];

        Assert.Empty(DuplicateGrouper.Group(items, level));
    }

    /// <summary>
    /// The edit can still look close enough to its original for a perceptual level, but that
    /// group is a lookalike, not identical files, whatever the stale hash says.
    /// </summary>
    [Fact]
    public void A_group_holding_an_edited_photo_is_never_exact()
    {
        List<ScannedPhoto> items =
        [
            Scanned(Photo("original"), Signature(0b0000), contentHash: "abc"),
            Scanned(Photo("edited", edited: true), Signature(0b0001), contentHash: "abc"),
        ];

        var group = Assert.Single(DuplicateGrouper.Group(items, DuplicateLevel.SamePhoto));

        Assert.Equal(2, group.Items.Count);
        Assert.False(group.IsExact);
    }

    // ---------- Same photo, re-encoded ----------

    [Fact]
    public void A_re_encoded_copy_groups_with_its_original()
    {
        // The WhatsApp case: a third of the pixels, a fifth of the bytes, same picture.
        List<ScannedPhoto> items =
        [
            Scanned(Photo("original", size: 5_000_000, width: 4000, height: 3000), Signature(0b0000)),
            Scanned(Photo("whatsapp", size: 900_000, width: 1600, height: 1200), Signature(0b0111)),
        ];

        var group = Assert.Single(DuplicateGrouper.Group(items, DuplicateLevel.SamePhoto));

        Assert.Equal(2, group.Items.Count);
        Assert.False(group.IsExact);
        Assert.Equal("original", group.Keeper.Id);
    }

    [Fact]
    public void An_unrelated_picture_stays_out()
    {
        List<ScannedPhoto> items =
        [
            Scanned(Photo("a"), Signature(0x0000_0000_0000_0000)),
            Scanned(Photo("b"), Signature(0x0F0F_0F0F_0F0F_0F0F)),
        ];

        Assert.Empty(DuplicateGrouper.Group(items, DuplicateLevel.SamePhoto));
    }

    [Fact]
    public void Colour_disagreement_keeps_lookalikes_apart()
    {
        byte[] mostlyRed = [.. Enumerable.Range(0, 64).Select(i => (byte)(i == 48 ? 255 : 0))];
        byte[] mostlyBlue = [.. Enumerable.Range(0, 64).Select(i => (byte)(i == 3 ? 255 : 0))];

        List<ScannedPhoto> items =
        [
            Scanned(Photo("a"), Signature(0b0000, histogram: mostlyRed)),
            Scanned(Photo("b"), Signature(0b0001, histogram: mostlyBlue)),
        ];

        Assert.Empty(DuplicateGrouper.Group(items, DuplicateLevel.SamePhoto));
    }

    /// <summary>
    /// The systematic failure mode: blank pages and dark frames all hash alike, so they are
    /// excluded rather than matched.
    /// </summary>
    [Fact]
    public void Flat_images_are_never_called_duplicates()
    {
        List<ScannedPhoto> items =
        [
            Scanned(Photo("blank-scan"), Signature(0b0000, detail: 3)),
            Scanned(Photo("dark-frame"), Signature(0b0000, detail: 2)),
        ];

        Assert.Empty(DuplicateGrouper.Group(items, DuplicateLevel.SamePhoto));
    }

    [Fact]
    public void Different_framing_is_a_different_picture()
    {
        List<ScannedPhoto> items =
        [
            Scanned(Photo("four-three", width: 4000, height: 3000), Signature(0b0000)),
            Scanned(Photo("sixteen-nine", width: 1920, height: 1080), Signature(0b0000)),
        ];

        Assert.Empty(DuplicateGrouper.Group(items, DuplicateLevel.SamePhoto));
    }

    [Fact]
    public void A_photo_never_groups_with_a_video()
    {
        List<ScannedPhoto> items =
        [
            Scanned(Photo("still", kind: MediaKind.Image), Signature(0b0000)),
            Scanned(Photo("clip", kind: MediaKind.Video), Signature(0b0000)),
        ];

        Assert.Empty(DuplicateGrouper.Group(items, DuplicateLevel.SamePhoto));
    }

    /// <summary>Three re-encodes of one original chain into a single group, not two pairs.</summary>
    [Fact]
    public void Grouping_is_transitive()
    {
        List<ScannedPhoto> items =
        [
            Scanned(Photo("a"), Signature(0b0000_0000)),
            Scanned(Photo("b"), Signature(0b0000_1111)),
            Scanned(Photo("c"), Signature(0b1111_1111)),
        ];

        // a and c are 8 bits apart — past the threshold — and only travel together via b.
        var group = Assert.Single(DuplicateGrouper.Group(items, DuplicateLevel.SamePhoto));

        Assert.Equal(["a", "b", "c"], group.Items.Select(p => p.Id).Order());
    }

    [Fact]
    public void Lone_photos_are_not_reported()
    {
        List<ScannedPhoto> items =
        [
            Scanned(Photo("a"), Signature(0x0000_0000_0000_0000)),
            Scanned(Photo("b"), Signature(0x0F0F_0F0F_0F0F_0F0F)),
            Scanned(Photo("c"), Signature(0xF0F0_F0F0_F0F0_F0F0)),
        ];

        Assert.Empty(DuplicateGrouper.Group(items, DuplicateLevel.SamePhoto));
    }

    // ---------- Adjusted copies ----------

    /// <summary>
    /// A quarter turn has to be confirmed by the same orientation that found it: the coarse
    /// hash compared upright can never agree with a sideways picture, so a rotated copy would
    /// be found and then silently rejected.
    /// </summary>
    [Fact]
    public void A_rotated_copy_groups_at_the_adjusted_level()
    {
        const ulong Upright = 0x0000_0000_0000_0000;
        const ulong Quarter = 0xFFFF_FFFF_0000_0000;
        const ulong Half = 0xFFFF_FFFF_FFFF_FFFF;
        const ulong ThreeQuarter = 0x0000_0000_FFFF_FFFF;

        List<ScannedPhoto> items =
        [
            Scanned(
                Photo("upright", width: 4000, height: 3000),
                Turning(Upright, Quarter, Half, ThreeQuarter)),
            Scanned(
                Photo("turned", width: 3000, height: 4000),
                Turning(Quarter, Half, ThreeQuarter, Upright)),
        ];

        var group = Assert.Single(DuplicateGrouper.Group(items, DuplicateLevel.Adjusted));
        Assert.Equal(2, group.Items.Count);

        // The level meant for re-encodes does not entertain rotation at all.
        Assert.Empty(DuplicateGrouper.Group(items, DuplicateLevel.SamePhoto));
    }

    [Fact]
    public void Recolouring_does_not_break_the_adjusted_level()
    {
        byte[] warm = [.. Enumerable.Range(0, 64).Select(i => (byte)(i == 48 ? 255 : 0))];
        byte[] cool = [.. Enumerable.Range(0, 64).Select(i => (byte)(i == 3 ? 255 : 0))];

        List<ScannedPhoto> items =
        [
            Scanned(Photo("original"), Signature(0b0000, histogram: warm)),
            Scanned(Photo("filtered"), Signature(0b0011, histogram: cool)),
        ];

        // Colours disagree completely, which is what a filter does — so "same photo" refuses
        // and "adjusted" accepts.
        Assert.Empty(DuplicateGrouper.Group(items, DuplicateLevel.SamePhoto));
        Assert.Single(DuplicateGrouper.Group(items, DuplicateLevel.Adjusted));
    }

    // ---------- Bursts ----------

    [Fact]
    public void Burst_frames_group_only_when_they_are_seconds_apart()
    {
        var signature = Signature(0b0000, detail: 50);
        var driftedApart = Signature(0b0011_1111_1111, detail: 50); // 10 bits: same scene, someone moved

        List<ScannedPhoto> sameBurst =
        [
            Scanned(Photo("frame-1", takenAt: "2026-07-01T12:00:00"), signature),
            Scanned(Photo("frame-2", takenAt: "2026-07-01T12:00:05"), driftedApart),
        ];

        List<ScannedPhoto> minutesApart =
        [
            Scanned(Photo("frame-1", takenAt: "2026-07-01T12:00:00"), signature),
            Scanned(Photo("frame-2", takenAt: "2026-07-01T12:05:00"), driftedApart),
        ];

        Assert.Single(DuplicateGrouper.Group(sameBurst, DuplicateLevel.SameMoment));
        Assert.Empty(DuplicateGrouper.Group(minutesApart, DuplicateLevel.SameMoment));
    }

    /// <summary>
    /// A re-encoded copy carries the date it arrived, not the date the shot was taken, so the
    /// levels meant for re-encodes must not look at time at all.
    /// </summary>
    [Fact]
    public void Same_photo_level_ignores_how_far_apart_the_dates_are()
    {
        List<ScannedPhoto> items =
        [
            Scanned(Photo("taken", takenAt: "2019-04-02T09:00:00"), Signature(0b0000)),
            Scanned(Photo("received", takenAt: "2026-07-01T12:00:00"), Signature(0b0011)),
        ];

        Assert.Single(DuplicateGrouper.Group(items, DuplicateLevel.SamePhoto));
    }

    // ---------- Videos ----------

    private static ImageSignature[] Frames(ulong hash, double detail = 50) =>
        [Signature(hash, detail), Signature(hash, detail), Signature(hash, detail), Signature(hash, detail)];

    [Fact]
    public void A_re_encoded_clip_groups_with_its_original()
    {
        // The video case: a fifth of the bytes, same running time, same frames.
        List<ScannedPhoto> items =
        [
            ScannedClip(Clip("original", durationMs: 42_000, size: 180_000_000), Frames(0b0000)),
            ScannedClip(Clip("shared", durationMs: 42_400, size: 24_000_000), Frames(0b0011)),
        ];

        var group = Assert.Single(DuplicateGrouper.Group(items, DuplicateLevel.SamePhoto));

        Assert.Equal("original", group.Keeper.Id);
        Assert.Equal(24_000_000, group.ReclaimableBytes);
    }

    [Fact]
    public void Clips_of_different_lengths_never_group_however_alike_the_frames_are()
    {
        List<ScannedPhoto> items =
        [
            ScannedClip(Clip("a", durationMs: 42_000), Frames(0b0000)),
            ScannedClip(Clip("b", durationMs: 58_000), Frames(0b0000)),
        ];

        Assert.Empty(DuplicateGrouper.Group(items, DuplicateLevel.SamePhoto));
    }

    [Fact]
    public void Clips_whose_frames_disagree_never_group_however_close_the_lengths_are()
    {
        List<ScannedPhoto> items =
        [
            ScannedClip(Clip("a", durationMs: 42_000), Frames(0x0000_0000_0000_0000)),
            ScannedClip(Clip("b", durationMs: 42_000), Frames(0x0F0F_0F0F_0F0F_0F0F)),
        ];

        Assert.Empty(DuplicateGrouper.Group(items, DuplicateLevel.SamePhoto));
    }

    /// <summary>Filters, bursts and sessions are questions about photographs, not clips.</summary>
    [Theory]
    [InlineData(DuplicateLevel.Adjusted)]
    [InlineData(DuplicateLevel.SameMoment)]
    [InlineData(DuplicateLevel.Similar)]
    public void The_photo_only_levels_leave_clips_alone(DuplicateLevel level)
    {
        List<ScannedPhoto> items =
        [
            ScannedClip(Clip("a", durationMs: 42_000), Frames(0b0000)),
            ScannedClip(Clip("b", durationMs: 42_000), Frames(0b0000)),
        ];

        Assert.Empty(DuplicateGrouper.Group(items, level));
    }

    [Fact]
    public void Byte_identical_clips_group_even_at_the_photo_only_levels()
    {
        // The exact tier is a floor under every level, and it does not care what kind of
        // file it is looking at.
        List<ScannedPhoto> items =
        [
            new() { Photo = Clip("a", durationMs: 42_000), ContentHash = "same" },
            new() { Photo = Clip("b", durationMs: 42_000), ContentHash = "same" },
        ];

        var group = Assert.Single(DuplicateGrouper.Group(items, DuplicateLevel.Similar));
        Assert.True(group.IsExact);
    }

    [Fact]
    public void A_clip_with_no_sampled_frames_only_ever_matches_on_bytes()
    {
        List<ScannedPhoto> items =
        [
            new() { Photo = Clip("a", durationMs: 42_000) },
            new() { Photo = Clip("b", durationMs: 42_000) },
        ];

        Assert.Empty(DuplicateGrouper.Group(items, DuplicateLevel.SamePhoto));
    }

    // ---------- Output shape ----------

    [Fact]
    public void Keeper_leads_the_group_and_the_rest_is_what_can_be_freed()
    {
        List<ScannedPhoto> items =
        [
            Scanned(Photo("copy", size: 300_000, width: 1600, height: 1200), Signature(0b0011)),
            Scanned(Photo("original", size: 5_000_000, width: 4000, height: 3000), Signature(0b0000)),
        ];

        var group = Assert.Single(DuplicateGrouper.Group(items, DuplicateLevel.SamePhoto));

        Assert.Equal("original", group.Items[0].Id);
        Assert.Equal(300_000, group.ReclaimableBytes);
        Assert.Equal(1, group.RedundantCount);
    }

    [Fact]
    public void Groups_lead_with_the_most_space_to_reclaim()
    {
        List<ScannedPhoto> items =
        [
            Scanned(Photo("small-a", size: 1_000), Signature(0b0000)),
            Scanned(Photo("small-b", size: 1_000), Signature(0b0001)),
            Scanned(Photo("big-a", size: 8_000_000), Signature(0x0F0F_0F0F_0F0F_0F0F)),
            Scanned(Photo("big-b", size: 8_000_000), Signature(0x0F0F_0F0F_0F0F_0F0E)),
        ];

        var groups = DuplicateGrouper.Group(items, DuplicateLevel.SamePhoto);

        Assert.Equal(2, groups.Count);
        Assert.True(groups[0].ReclaimableBytes > groups[1].ReclaimableBytes);
    }

    [Fact]
    public void A_group_is_only_exact_when_every_member_shares_the_bytes()
    {
        List<ScannedPhoto> items =
        [
            Scanned(Photo("a"), Signature(0b0000), contentHash: "same"),
            Scanned(Photo("b"), Signature(0b0000), contentHash: "same"),
            Scanned(Photo("c"), Signature(0b0011), contentHash: "different"),
        ];

        var group = Assert.Single(DuplicateGrouper.Group(items, DuplicateLevel.SamePhoto));

        Assert.Equal(3, group.Items.Count);
        Assert.False(group.IsExact);
    }

    [Fact]
    public void Items_without_a_signature_only_ever_match_on_bytes()
    {
        List<ScannedPhoto> items =
        [
            Scanned(Photo("unreadable-a")),
            Scanned(Photo("unreadable-b")),
        ];

        Assert.Empty(DuplicateGrouper.Group(items, DuplicateLevel.Similar));
    }
}
