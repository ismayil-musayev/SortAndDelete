using SortAndDelete.Models;

namespace SortAndDelete.Logic;

/// <summary>
/// Whether what is ticked on the duplicates screen can still go to the bin once the library has
/// been read again since its groups were found.
///
/// A tick says "this copy can go, the others in its group stay". The screen looks at the library
/// on a visit and when the app comes back, but another app can change it while the screen is up,
/// which the gallery hears of from its change watcher and the screen doesn't. A copy deleted or
/// edited there leaves the one ticked beside it as, perhaps, the only one left. So before
/// anything is moved, every copy of the groups something is ticked in is looked up in the library
/// as it is now. Whatever else changed there (a screenshot, a download) has no say in it.
/// </summary>
public static class TickedGroups
{
    /// <summary>
    /// Every copy, ticked or not, of the groups in <paramref name="onScreen"/> with something
    /// ticked: the ones a move to the bin acts on, and the ones it counts on staying.
    /// </summary>
    public static List<ItemTick> Copies(IEnumerable<GroupTicks> onScreen) =>
        [.. onScreen
            .Where(group => group.Items.Any(item => item.IsTicked))
            .SelectMany(group => group.Items)];

    /// <summary>
    /// Whether each of <paramref name="copies"/> is still in the library as it went on screen:
    /// <paramref name="find"/> has it (it gives null for an id the library doesn't have), in the
    /// state it was shown in (<see cref="PhotoVersion"/>). The size is part of that, so where sizes
    /// are looked up later (iOS) they must be filled in first.
    /// </summary>
    public static bool AreAsShown(IEnumerable<ItemTick> copies, Func<string, PhotoAsset?> find) =>
        copies.All(copy => find(copy.PhotoId) is { } now && PhotoVersion.Of(now) == copy.Version);
}
