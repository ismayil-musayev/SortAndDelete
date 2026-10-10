using SortAndDelete.Logic;
using SortAndDelete.Models;
using Xunit;

namespace SortAndDelete.Tests;

public class LoadDecisionTests
{
    /// <summary>The list the groups on screen were found in.</summary>
    private static readonly IReadOnlyList<PhotoAsset> Scanned = [new() { Id = "1" }, new() { Id = "2" }];

    private static HashSet<string> Bin(params string[] ids) => new(ids, StringComparer.Ordinal);

    /// <summary>A load with groups on screen, found in <see cref="Scanned"/> with "a" in the bin.</summary>
    private static LoadAction Decide(
        IReadOnlyList<PhotoAsset>? photos = null,
        HashSet<string>? binned = null,
        bool fromScratch = false,
        bool busy = false) =>
        LoadDecision.For(Scanned, Bin("a"), photos ?? Scanned, binned ?? Bin("a"), fromScratch, busy);

    [Fact]
    public void The_same_list_and_the_same_bin_keep_the_groups()
    {
        Assert.Equal(LoadAction.Keep, Decide());
        Assert.True(LoadDecision.ChecksFirst(Scanned, fromScratch: false));
    }

    [Fact]
    public void With_no_results_yet_it_scans()
    {
        Assert.Equal(LoadAction.Scan, LoadDecision.For(null, Bin(), Scanned, Bin(), fromScratch: false, busy: false));
        Assert.False(LoadDecision.ChecksFirst(null, fromScratch: false));
    }

    /// <summary>The same instance is the only proof that the library wasn't reloaded.</summary>
    [Fact]
    public void A_reloaded_list_needs_a_scan_even_with_the_same_photos_in_it()
    {
        IReadOnlyList<PhotoAsset> reloaded = [.. Scanned];

        Assert.Equal(LoadAction.Scan, Decide(photos: reloaded));
    }

    /// <summary>What went to the bin from another screen only has to come out of the groups.</summary>
    [Fact]
    public void A_bin_that_only_grew_regroups()
    {
        Assert.Equal(LoadAction.Regroup, Decide(binned: Bin("a", "b")));
    }

    /// <summary>The last scan left it out, so no regroup could bring it back.</summary>
    [Fact]
    public void Something_leaving_the_bin_needs_a_scan()
    {
        Assert.Equal(LoadAction.Scan, Decide(binned: Bin()));
    }

    /// <summary>The bin is no smaller, but it isn't the old one grown.</summary>
    [Fact]
    public void Something_leaving_the_bin_as_other_things_go_in_still_needs_a_scan()
    {
        Assert.Equal(LoadAction.Scan, Decide(binned: Bin("b")));
        Assert.Equal(LoadAction.Scan, Decide(binned: Bin("b", "c")));
    }

    /// <summary>
    /// A move to the bin under way re-derives the groups itself. A scan would take them away
    /// from under it, whatever has changed.
    /// </summary>
    [Fact]
    public void While_items_are_moving_to_the_bin_the_groups_are_kept()
    {
        Assert.Equal(LoadAction.Keep, Decide(busy: true));
        Assert.Equal(LoadAction.Keep, Decide(binned: Bin("a", "b"), busy: true));
        Assert.Equal(LoadAction.Keep, Decide(binned: Bin(), busy: true));
        Assert.Equal(LoadAction.Keep, Decide(photos: [.. Scanned], busy: true));
    }

    [Fact]
    public void Asked_to_start_over_it_scans_though_the_groups_still_hold()
    {
        Assert.Equal(LoadAction.Scan, Decide(fromScratch: true));
        Assert.False(LoadDecision.ChecksFirst(Scanned, fromScratch: true));
    }
}
