using SortAndDelete.Logic;
using SortAndDelete.Models;
using Xunit;

namespace SortAndDelete.Tests;

public class HashAdoptionTests
{
    /// <summary>A file's stamp, as Android's DATE_MODIFIED gives it.</summary>
    private static readonly DateTime Written = new(2026, 7, 1, 12, 0, 5, DateTimeKind.Utc);

    /// <summary>An Android file, which always carries a stamp.</summary>
    private static PhotoAsset File(string id, long size = 5_000_000) => new()
    {
        Id = id,
        SizeBytes = size,
        FileModifiedAt = Written,
    };

    /// <summary>An iOS item, which never does.</summary>
    private static PhotoAsset Asset(string id, long size = 5_000_000) => new()
    {
        Id = id,
        SizeBytes = size,
    };

    /// <summary>A hash cached before rows carried the file's stamp, as it reads back now.</summary>
    private static ContentHashRecord Unstamped(string id, string hash, long size = 5_000_000) =>
        new() { PhotoId = id, SizeBytes = size, Hash = hash };

    private static HashAdoption Adopt(IReadOnlyList<PhotoAsset> collisions, params ContentHashRecord[] rows) =>
        HashAdoption.For(collisions, rows.ToDictionary(row => row.PhotoId, StringComparer.Ordinal));

    private static string[] Ids(IEnumerable<PhotoAsset> photos) => [.. photos.Select(p => p.Id).Order()];

    private static string[] Ids(IEnumerable<ContentHashRecord> rows) => [.. rows.Select(r => r.PhotoId).Order()];

    /// <summary>
    /// The case this exists for: two identical files hashed before the update, one rotated in
    /// place since. Adopted, the old hash would go on calling the rotated one an identical file
    /// of its twin, and offer one of them for the bin.
    /// </summary>
    [Fact]
    public void Twins_hashed_before_stamping_are_read_again()
    {
        var adoption = Adopt([File("1"), File("2")], Unstamped("1", "ABC"), Unstamped("2", "ABC"));

        Assert.Equal(["1", "2"], Ids(adoption.Reread));
        Assert.Empty(adoption.Adopted);
    }

    /// <summary>
    /// A hash that nothing could pair with is taken at its word, so updating costs no reads, and
    /// saved with the file's stamp so that the next in-place edit shows.
    /// </summary>
    [Fact]
    public void A_hash_from_before_stamping_that_matches_nothing_is_adopted_with_the_stamp()
    {
        var adoption = Adopt([File("1"), File("2")], Unstamped("1", "ABC"), Unstamped("2", "DEF"));

        Assert.Empty(adoption.Reread);
        Assert.Equal(["1", "2"], Ids(adoption.Adopted));
        Assert.Equal(["ABC", "DEF"], adoption.Adopted.Select(row => row.Hash).Order());
        Assert.All(adoption.Adopted, row => Assert.Equal(Written.Ticks, row.FileModifiedAtTicks));
    }

    /// <summary>
    /// A stamped hash was taken from the file as it is now, so of a stamped and an unstamped twin
    /// only the unstamped one is in doubt.
    /// </summary>
    [Fact]
    public void Of_a_stamped_and_an_unstamped_twin_only_the_unstamped_one_is_read_again()
    {
        var stamped = File("1");

        var adoption = Adopt([stamped, File("2")], ContentHashRecord.From(stamped, "ABC"), Unstamped("2", "ABC"));

        Assert.Equal(["2"], Ids(adoption.Reread));
        Assert.Empty(adoption.Adopted);
    }

    /// <summary>
    /// Two hashes claiming a pair say nothing about the other files of their length. Those
    /// can't make a pair with anything, so they keep their hashes and cost no read.
    /// </summary>
    [Fact]
    public void Only_the_hashes_claiming_a_pair_are_read_again_and_the_rest_of_their_length_adopted()
    {
        var adoption = Adopt(
            [File("1"), File("2"), File("3"), File("4")],
            Unstamped("1", "ABC"), Unstamped("2", "ABC"), Unstamped("3", "DEF"), Unstamped("4", "GHI"));

        Assert.Equal(["1", "2"], Ids(adoption.Reread));
        Assert.Equal(["3", "4"], Ids(adoption.Adopted));
        Assert.Equal(["DEF", "GHI"], adoption.Adopted.Select(row => row.Hash).Order());
        Assert.All(adoption.Adopted, row => Assert.Equal(Written.Ticks, row.FileModifiedAtTicks));
    }

    /// <summary>
    /// The same beside a stamped twin: the unstamped one of the pair is read, and a third hash
    /// of the length is adopted. A pair of stamped hashes puts nothing in doubt at all.
    /// </summary>
    [Fact]
    public void A_third_hash_is_adopted_beside_a_pair_whether_the_pair_is_stamped_or_not()
    {
        var first = File("1");
        var second = File("2");

        var halfStamped = Adopt(
            [first, second, File("3")],
            ContentHashRecord.From(first, "ABC"), Unstamped("2", "ABC"), Unstamped("3", "DEF"));
        var stamped = Adopt(
            [first, second, File("3")],
            ContentHashRecord.From(first, "ABC"), ContentHashRecord.From(second, "ABC"), Unstamped("3", "DEF"));

        Assert.Equal(["2"], Ids(halfStamped.Reread));
        Assert.Equal(["3"], Ids(halfStamped.Adopted));
        Assert.Empty(stamped.Reread);
        Assert.Equal(["3"], Ids(stamped.Adopted));
    }

    /// <summary>
    /// A file of the same length read anyway, one new since the last scan or one whose hash is
    /// stale, could turn out to be what an unstamped hash describes, perhaps from before a
    /// rotation, so that hash is read again too. Other lengths aren't affected.
    /// </summary>
    [Fact]
    public void A_file_being_read_puts_the_unstamped_hashes_of_its_length_in_doubt()
    {
        var stamped = File("6", size: 4_000_000);

        var adoption = Adopt(
            [File("1"), File("2"), File("3", size: 6_000_000), File("4", size: 6_000_000), File("5", size: 4_000_000), stamped],
            Unstamped("1", "ABC"),                  // beside 2, which has no hash yet
            Unstamped("3", "DEF", size: 6_000_000), // beside 4, whose hash is stale
            Unstamped("4", "GHI", size: 3_000_000),
            Unstamped("5", "JKL", size: 4_000_000), // beside a stamped hash, and a different one
            ContentHashRecord.From(stamped, "MNO"));

        Assert.Equal(["1", "3"], Ids(adoption.Reread));
        Assert.Equal(["5"], Ids(adoption.Adopted));
    }

    /// <summary>
    /// That holds for every unstamped hash of the length, not only the ones claiming a pair
    /// already: the file being read could turn out to match any of them.
    /// </summary>
    [Fact]
    public void A_file_being_read_puts_a_hash_nothing_shares_in_doubt_too()
    {
        var adoption = Adopt(
            [File("1"), File("2"), File("3"), File("4")], // 4 has no hash yet
            Unstamped("1", "ABC"), Unstamped("2", "ABC"), Unstamped("3", "DEF"));

        Assert.Equal(["1", "2", "3"], Ids(adoption.Reread));
        Assert.Empty(adoption.Adopted);
    }

    /// <summary>
    /// iOS never stamps a file, as it never rewrites an original, so its hashes are never in
    /// doubt this way: identical files stay identical with no second read, even beside a new one.
    /// </summary>
    [Fact]
    public void Hashes_of_unstamped_files_are_left_as_they_are()
    {
        var adoption = Adopt([Asset("1"), Asset("2"), Asset("3")], Unstamped("1", "ABC"), Unstamped("2", "ABC"));

        Assert.Empty(adoption.Reread);
        Assert.Empty(adoption.Adopted);
    }
}
