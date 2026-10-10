using SortAndDelete.Models;

namespace SortAndDelete.Logic;

/// <summary>What a load of the duplicates screen does about the groups already on it.</summary>
public enum LoadAction
{
    /// <summary>They still hold, so nothing is touched, what is ticked included.</summary>
    Keep,

    /// <summary>
    /// They hold, less what has gone to the bin from elsewhere: grouped again without those,
    /// reading nothing.
    /// </summary>
    Regroup,

    /// <summary>There are none, or they can't be taken at their word any more: scan.</summary>
    Scan,
}

/// <summary>
/// Whether the groups on the duplicates screen still hold when it loads again, on coming back to
/// it or to the app.
///
/// They were found in one photo list, less what was headed for the bin. The gallery hands out the
/// same list instance until it reloads the library, so the instance is the test for the first. The
/// bin can change from other screens: what has gone in since only has to be taken out of the
/// groups, while something restored needs a scan, as the last one left it out.
/// </summary>
public static class LoadDecision
{
    /// <summary>
    /// Whether there are groups to check before anything is read: some were found, and the user
    /// hasn't asked to start over. With none the scan can show at once.
    /// </summary>
    public static bool ChecksFirst(IReadOnlyList<PhotoAsset>? resultsFrom, bool fromScratch) =>
        !fromScratch && resultsFrom is not null;

    /// <summary>What this load does, once it has the photo list and the bin as they are now.</summary>
    /// <param name="resultsFrom">The photo list the groups were found in, null with none to keep.</param>
    /// <param name="resultsBinned">What was headed for the bin when they were found, plus what the screen sent there since.</param>
    /// <param name="photos">The photo list as the gallery hands it out now.</param>
    /// <param name="binned">What is headed for the bin now.</param>
    /// <param name="fromScratch">The user asked to scan everything again.</param>
    /// <param name="busy">The screen is moving items to the bin.</param>
    public static LoadAction For(
        IReadOnlyList<PhotoAsset>? resultsFrom,
        IReadOnlySet<string> resultsBinned,
        IReadOnlyList<PhotoAsset> photos,
        IReadOnlySet<string> binned,
        bool fromScratch,
        bool busy)
    {
        if (!ChecksFirst(resultsFrom, fromScratch))
            return LoadAction.Scan;

        // Moving items to the bin re-derives the groups itself; the next visit catches up.
        if (busy)
            return LoadAction.Keep;

        if (!ReferenceEquals(photos, resultsFrom) || !binned.IsSupersetOf(resultsBinned))
            return LoadAction.Scan;

        return binned.Count > resultsBinned.Count ? LoadAction.Regroup : LoadAction.Keep;
    }
}
