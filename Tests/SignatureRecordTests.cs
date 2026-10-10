using SortAndDelete.Models;
using Xunit;

namespace SortAndDelete.Tests;

public class SignatureRecordTests
{
    // Modification times as iOS reports them, in UTC.
    private static readonly DateTime Imported = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Edited = new(2026, 8, 15, 9, 30, 0, DateTimeKind.Utc);

    // File times as Android's DATE_MODIFIED gives them, in UTC.
    private static readonly DateTime Written = new(2026, 7, 1, 12, 0, 5, DateTimeKind.Utc);
    private static readonly DateTime Rotated = new(2026, 8, 15, 9, 31, 0, DateTimeKind.Utc);

    private static PhotoAsset Photo(
        long size = 5_000_000,
        string takenAt = "2026-07-01T12:00:00",
        DateTime modifiedAt = default,
        DateTime fileModifiedAt = default) => new()
    {
        Id = "42",
        SizeBytes = size,
        TakenAt = DateTime.Parse(takenAt),
        ModifiedAt = modifiedAt,
        FileModifiedAt = fileModifiedAt,
    };

    /// <summary>A content hash saved before rows carried the file's stamp, as it reads back now.</summary>
    private static ContentHashRecord HashCachedBeforeStamping() =>
        new() { PhotoId = "42", SizeBytes = 5_000_000, Hash = "ABC" };

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

    /// <summary>
    /// An Android rotation rewrites the file in place and can keep its size and capture date, so
    /// only the file's stamp shows that the bytes the hash was taken from are gone. Trusted, the
    /// old hash would still call the rotated file identical to its unrotated twin.
    /// </summary>
    [Fact]
    public void A_content_hash_goes_stale_when_the_file_is_rewritten_in_place()
    {
        var record = ContentHashRecord.From(Photo(fileModifiedAt: Written), "ABC");

        Assert.Equal(Written.Ticks, record.FileModifiedAtTicks);
        Assert.True(record.IsCurrentFor(Photo(fileModifiedAt: Written)));
        Assert.False(record.IsCurrentFor(Photo(fileModifiedAt: Rotated)));
    }

    /// <summary>
    /// A hash cached before files were stamped has nothing to compare, so it is taken on its size
    /// as before. Throwing it away instead would have every Android user re-read each file that
    /// shares a size with another after updating.
    /// </summary>
    [Fact]
    public void A_content_hash_from_before_stamping_is_adopted_by_a_stamped_file()
    {
        var record = HashCachedBeforeStamping();

        Assert.True(record.IsCurrentFor(Photo(fileModifiedAt: Written)));
        Assert.False(record.IsCurrentFor(Photo(size: 4_000_000, fileModifiedAt: Written)));
    }

    /// <summary>
    /// iOS never stamps a file, since it never rewrites an original, so its hashes stay on size
    /// alone: an edit or a favourite leaves them current, a different length does not.
    /// </summary>
    [Fact]
    public void A_content_hash_of_an_unstamped_file_goes_by_size_alone()
    {
        var record = ContentHashRecord.From(Photo(modifiedAt: Imported), "ABC");

        Assert.Equal(0, record.FileModifiedAtTicks);
        Assert.True(record.IsCurrentFor(Photo(modifiedAt: Edited)));
        Assert.False(record.IsCurrentFor(Photo(size: 4_000_000, modifiedAt: Imported)));
        Assert.False(record.NeedsStampFrom(Photo(modifiedAt: Edited)));
    }

    /// <summary>
    /// The scan saves an unstamped row again with the file's stamp. Only a row that still holds
    /// takes one, as a stale one is hashed again instead, and only from a file with one to give.
    /// </summary>
    [Fact]
    public void Only_a_current_unstamped_content_hash_takes_the_file_stamp()
    {
        var record = HashCachedBeforeStamping();

        Assert.True(record.NeedsStampFrom(Photo(fileModifiedAt: Written)));
        Assert.False(record.NeedsStampFrom(Photo()));
        Assert.False(record.NeedsStampFrom(Photo(size: 4_000_000, fileModifiedAt: Written)));
        Assert.False(ContentHashRecord.From(Photo(fileModifiedAt: Written), "ABC")
            .NeedsStampFrom(Photo(fileModifiedAt: Rotated)));
    }

    /// <summary>Adopted on its size once, a hash saved again with the stamp notices the next rewrite.</summary>
    [Fact]
    public void A_restamped_content_hash_notices_the_next_rewrite()
    {
        var file = Photo(fileModifiedAt: Written);

        var restamped = ContentHashRecord.From(file, HashCachedBeforeStamping().Hash);

        Assert.Equal("ABC", restamped.Hash);
        Assert.True(restamped.IsCurrentFor(file));
        Assert.False(restamped.NeedsStampFrom(file));
        Assert.False(restamped.IsCurrentFor(Photo(fileModifiedAt: Rotated)));
    }

    /// <summary>
    /// A hash taken now is of the file's own bytes, and says so. One cached before rows said so
    /// may be of a file Android handed over with its location blanked: it is still current, as
    /// the file hasn't changed, but it can't make the file an identical file of anything.
    /// </summary>
    [Fact]
    public void Only_a_hash_known_to_be_of_the_files_own_bytes_proves_identity()
    {
        var file = Photo(fileModifiedAt: Written);
        var taken = ContentHashRecord.From(file, "ABC");
        var cachedBefore = new ContentHashRecord
        {
            PhotoId = file.Id,
            SizeBytes = file.SizeBytes,
            FileModifiedAtTicks = file.FileModifiedAt.Ticks,
            Hash = "ABC",
        };

        Assert.True(taken.Original);
        Assert.True(taken.ProvesIdentityOf(file));

        Assert.False(cachedBefore.Original);
        Assert.True(cachedBefore.IsCurrentFor(file));
        Assert.False(cachedBefore.ProvesIdentityOf(file));
    }

    /// <summary>A hash of the original proves nothing about the file once the file has changed.</summary>
    [Fact]
    public void A_hash_of_the_original_stops_proving_identity_when_the_file_is_rewritten()
    {
        var record = ContentHashRecord.From(Photo(fileModifiedAt: Written), "ABC");

        Assert.False(record.ProvesIdentityOf(Photo(fileModifiedAt: Rotated)));
        Assert.False(record.ProvesIdentityOf(Photo(size: 4_000_000, fileModifiedAt: Written)));
    }
}
