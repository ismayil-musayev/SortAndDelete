using SortAndDelete.Logic;
using SortAndDelete.Models;
using Xunit;

namespace SortAndDelete.Tests;

/// <summary>
/// The duplicates screen takes the photo list's instance for "the library hasn't been read again"
/// (<see cref="LoadDecision"/>), and the gallery keeps its lists as a <see cref="VersionedLoad{T}"/>
/// of <see cref="LibrarySnapshot"/>s, marked stale or invalidated from other screens and by the
/// change watcher. These hold the two together: the instance changes when the gallery reads the
/// library again, and only then.
/// </summary>
public class ReloadSignalTests
{
    private static readonly HashSet<string> EmptyBin = new(StringComparer.Ordinal);

    private int _loads;

    /// <summary>The gallery's lists, each load reading the same two photos into a new snapshot.</summary>
    private VersionedLoad<LibrarySnapshot> Gallery() => new(() =>
    {
        _loads++;
        return Task.FromResult(new LibrarySnapshot([new PhotoAsset { Id = "1" }, new PhotoAsset { Id = "2" }], _ => []));
    });

    /// <summary>The photo list as the gallery hands it out now. These loads end at once.</summary>
    private static IReadOnlyList<PhotoAsset> Photos(VersionedLoad<LibrarySnapshot> gallery)
    {
        var read = gallery.GetAsync();
        Assert.True(read.IsCompletedSuccessfully, $"The read hasn't ended: {read.Status}");
        return read.Result.Photos;
    }

    /// <summary>What a visit does about groups found in <paramref name="scanned"/>, with the bin as it was.</summary>
    private static LoadAction Visit(IReadOnlyList<PhotoAsset> scanned, VersionedLoad<LibrarySnapshot> gallery) =>
        LoadDecision.For(scanned, EmptyBin, Photos(gallery), EmptyBin, fromScratch: false, busy: false);

    /// <summary>Home reads the lists on every visit, and its reads leave them as they are.</summary>
    [Fact]
    public void Reads_between_reloads_hand_out_the_same_list_and_the_groups_are_kept()
    {
        var gallery = Gallery();
        var scanned = Photos(gallery);

        _ = Photos(gallery);
        _ = Photos(gallery);

        Assert.Equal(LoadAction.Keep, Visit(scanned, gallery));
        Assert.Equal(1, _loads);
    }

    /// <summary>The change watcher's mark, and the one for coming back to the app.</summary>
    [Fact]
    public void A_library_marked_stale_is_read_again_and_the_next_visit_scans()
    {
        var gallery = Gallery();
        var scanned = Photos(gallery);

        gallery.MarkStale();

        Assert.Equal(LoadAction.Scan, Visit(scanned, gallery));
        Assert.Equal(2, _loads);
    }

    /// <summary>Emptying the bin, restoring from the system trash and moving all invalidate.</summary>
    [Fact]
    public void An_invalidated_library_is_read_again_and_the_next_visit_scans()
    {
        var gallery = Gallery();
        var scanned = Photos(gallery);

        gallery.Invalidate();

        Assert.Equal(LoadAction.Scan, Visit(scanned, gallery));
        Assert.Equal(2, _loads);
    }

    /// <summary>One scan per reload: the visits after it find the list the scan was made on.</summary>
    [Fact]
    public void After_the_scan_that_follows_a_reload_the_groups_are_kept_again()
    {
        var gallery = Gallery();
        _ = Photos(gallery);
        gallery.MarkStale();

        var rescanned = Photos(gallery);

        Assert.Equal(LoadAction.Keep, Visit(rescanned, gallery));
        Assert.Equal(2, _loads);
    }
}
