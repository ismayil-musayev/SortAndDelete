using SortAndDelete.Models;
using Xunit;

namespace SortAndDelete.Tests;

public class SignatureRecordTests
{
    private static PhotoAsset Photo(long size = 5_000_000, string takenAt = "2026-07-01T12:00:00") => new()
    {
        Id = "42",
        SizeBytes = size,
        TakenAt = DateTime.Parse(takenAt),
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

    [Fact]
    public void Video_frames_are_stored_apart_from_each_other()
    {
        Assert.Equal("42", SignatureRecord.KeyFor("42", 0));
        Assert.Equal("42#1", SignatureRecord.KeyFor("42", 1));
        Assert.NotEqual(SignatureRecord.KeyFor("42", 1), SignatureRecord.KeyFor("42", 2));
    }
}
