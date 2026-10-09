using SortAndDelete.Models;
using Xunit;

namespace SortAndDelete.Tests;

public class SignatureRecordTests
{
    // Modification times as iOS reports them, in UTC.
    private static readonly DateTime Imported = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Edited = new(2026, 8, 15, 9, 30, 0, DateTimeKind.Utc);

    private static PhotoAsset Photo(
        long size = 5_000_000,
        string takenAt = "2026-07-01T12:00:00",
        DateTime modifiedAt = default) => new()
    {
        Id = "42",
        SizeBytes = size,
        TakenAt = DateTime.Parse(takenAt),
        ModifiedAt = modifiedAt,
    };

    private static ImageSignature Signature(ulong seed) => new()
    {
        DHash = seed,
        DHash90 = seed ^ 0x1111_1111_1111_1111,
        DHash180 = seed ^ 0x2222_2222_2222_2222,
        DHash270 = seed ^ 0x3333_3333_3333_3333,
        PHash = seed ^ 0x4444_4444_4444_4444,
        PHash90 = seed ^ 0x5555_5555_5555_5555,
        PHash180 = seed ^ 0x6666_6666_6666_6666,
        PHash270 = seed ^ 0x7777_7777_7777_7777,
        Histogram = [.. Enumerable.Range(0, 64).Select(i => (byte)i)],
        Detail = 37.25,
    };

    /// <summary>
    /// Hashes are unsigned and SQLite integers are signed, so anything with the top bit set
    /// has to survive the round trip unchanged rather than saturating or throwing.
    /// </summary>
    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(ulong.MaxValue)]
    [InlineData(0x8000_0000_0000_0000UL)]
    [InlineData(0xDEAD_BEEF_CAFE_F00DUL)]
    public void Hashes_survive_the_trip_through_signed_storage(ulong seed)
    {
        var original = Signature(seed);

        var restored = SignatureRecord.From("42", 0, Photo(), original).ToSignature();

        Assert.Equal(original.DHash, restored.DHash);
        Assert.Equal(original.DHash90, restored.DHash90);
        Assert.Equal(original.DHash180, restored.DHash180);
        Assert.Equal(original.DHash270, restored.DHash270);
        Assert.Equal(original.PHash, restored.PHash);
        Assert.Equal(original.PHash90, restored.PHash90);
        Assert.Equal(original.PHash180, restored.PHash180);
        Assert.Equal(original.PHash270, restored.PHash270);
        Assert.Equal(original.Histogram, restored.Histogram);
        Assert.Equal(original.Detail, restored.Detail, precision: 10);
    }

    [Fact]
    public void A_fingerprint_goes_stale_when_the_file_changes_under_it()
    {
        var record = SignatureRecord.From("42", 0, Photo(size: 5_000_000), Signature(1));

        Assert.True(record.IsCurrentFor(Photo(size: 5_000_000)));
        Assert.False(record.IsCurrentFor(Photo(size: 4_000_000)));
        Assert.False(record.IsCurrentFor(Photo(takenAt: "2020-01-01T00:00:00")));
    }

    /// <summary>
    /// An iOS edit leaves the original alone, so its size and capture date both still hold and
    /// only the modification time shows that the picture changed.
    /// </summary>
    [Fact]
    public void A_fingerprint_goes_stale_when_the_item_is_edited()
    {
        var record = SignatureRecord.From("42", 0, Photo(modifiedAt: Imported), Signature(1));

        Assert.True(record.IsCurrentFor(Photo(modifiedAt: Imported)));
        Assert.False(record.IsCurrentFor(Photo(modifiedAt: Edited)));
    }

    /// <summary>
    /// Android never sets a modification time, and a row cached before there was one reads
    /// back as 0. The two have to keep matching, or every upgrade would throw the cache away.
    /// </summary>
    [Fact]
    public void A_record_without_a_modification_time_is_current_for_an_untracked_photo()
    {
        var photo = Photo();
        var cachedBeforeTracking = new SignatureRecord
        {
            Key = "42",
            PhotoId = "42",
            SizeBytes = photo.SizeBytes,
            TakenAtTicks = photo.TakenAt.Ticks,
            ModifiedAtTicks = 0,
        };

        Assert.True(cachedBeforeTracking.IsCurrentFor(photo));
    }

    [Fact]
    public void Video_frames_are_stored_apart_from_each_other()
    {
        Assert.Equal("42", SignatureRecord.KeyFor("42", 0));
        Assert.Equal("42#1", SignatureRecord.KeyFor("42", 1));
        Assert.NotEqual(SignatureRecord.KeyFor("42", 1), SignatureRecord.KeyFor("42", 2));
    }

    // ---------- Content hashes ----------

    [Fact]
    public void A_content_hash_goes_stale_when_the_file_changes_under_it()
    {
        var record = ContentHashRecord.From(Photo(size: 5_000_000), "ABC");

        Assert.True(record.IsCurrentFor(Photo(size: 5_000_000)));
        Assert.False(record.IsCurrentFor(Photo(size: 4_000_000)));
    }

    /// <summary>
    /// The hash is of the untouched original, which favouriting and other metadata changes
    /// leave alone. Expiring it then would re-read the whole original, perhaps from iCloud.
    /// </summary>
    [Fact]
    public void A_content_hash_outlasts_a_metadata_change()
    {
        var record = ContentHashRecord.From(Photo(modifiedAt: Imported), "ABC");

        Assert.True(record.IsCurrentFor(Photo(modifiedAt: Imported.AddDays(1))));
    }
}
