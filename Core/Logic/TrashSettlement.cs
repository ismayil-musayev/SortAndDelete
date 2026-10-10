using SortAndDelete.Models;

namespace SortAndDelete.Logic;

/// <summary>
/// Settles what a request to empty the bin, or to restore from the system trash, did with each
/// item, from what the request itself did and what the library says afterwards. A request's own
/// answer is no guide: Android's consent screens report success either way. And an item the
/// library doesn't list is not gone for that: MediaStore hides the rows of an SD card that isn't
/// in the phone, and of every photo outside the ones the user let the app see, exactly as if they
/// had been deleted. Recording such an item as deleted would count it as freed and keep it out of
/// every deck while its file is still there. So an item counts as removed only when it is found
/// in the system trash, or when the request acted on it (it was in the library when the request
/// began, and the request deleted it, or trashed it with the user's consent) and the library,
/// still able to see where it was, no longer has it.
/// </summary>
public static class TrashSettlement
{
    /// <summary>What became of each of <paramref name="asked"/> after a request to remove them.</summary>
    /// <param name="asked">The items the bin held.</param>
    /// <param name="handledByRequest">
    /// The ones the request itself took out of the library, each of which it found there first:
    /// deleted for good and then checked (Android 10: the file is gone and the library has
    /// dropped it), or sent to the system trash with the user's consent (Android 11+).
    /// </param>
    /// <param name="inLibrary">Which of them the library still has now; null when it couldn't be asked.</param>
    /// <param name="inSystemTrash">
    /// Which of them are in the system trash now; null when it couldn't be asked. Empty on
    /// Android 10, which has none.
    /// </param>
    /// <param name="isOnStorageThatIsThere">
    /// Whether the item's storage volume is mounted now: true or false when the volume it was on
    /// is known, null when it isn't (a decision recorded before volumes were).
    /// </param>
    /// <param name="someStorageIsAway">
    /// Whether a volume the phone knows isn't mounted now, which is where an item of unknown
    /// volume may be.
    /// </param>
    /// <param name="seesWholeLibrary">
    /// Whether the app has access to every photo. With access to selected photos only (Android
    /// 14+), or none, an item the library doesn't list may simply be outside what the app may see.
    /// </param>
    public static TrashResult Settle(
        IReadOnlyList<string> asked,
        IReadOnlySet<string> handledByRequest,
        IReadOnlySet<string>? inLibrary,
        IReadOnlySet<string>? inSystemTrash,
        Func<string, bool?> isOnStorageThatIsThere,
        bool someStorageIsAway,
        bool seesWholeLibrary)
    {
        var removed = new HashSet<string>(StringComparer.Ordinal);
        var missing = new HashSet<string>(StringComparer.Ordinal);
        var unreachable = new HashSet<string>(StringComparer.Ordinal);
        var left = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in asked.Distinct(StringComparer.Ordinal))
        {
            bool handled = handledByRequest.Contains(id);

            // The library has the last word: what it still lists was not removed, whatever the
            // request believes. And without its word nothing is taken for gone.
            if (inLibrary is null || inLibrary.Contains(id))
                left.Add(id);
            else if (inSystemTrash is not null && inSystemTrash.Contains(id))
                removed.Add(id);
            else if (handled)
            {
                // It was in the library when the request began, the request acted on it, and it
                // has left the library. Unless its card went out meanwhile (or may have: its
                // volume isn't known, and one is away), that is the request's doing: deleted, or
                // trashed where the trash can't be listed.
                if (IsOutOfReach(isOnStorageThatIsThere(id), someStorageIsAway))
                    unreachable.Add(id);
                else
                    removed.Add(id);
            }
            else if (inSystemTrash is null)
                left.Add(id); // nothing says where it is
            else if (!seesWholeLibrary || IsOutOfReach(isOnStorageThatIsThere(id), someStorageIsAway))
                unreachable.Add(id);
            else
                missing.Add(id);
        }

        return new TrashResult(removed, missing, unreachable, left);
    }

    /// <summary>What became of an item after a request to restore it from the system trash.</summary>
    /// <param name="inLibrary">Whether the library has it now; null when it couldn't be asked.</param>
    /// <param name="inSystemTrash">Whether it is in the system trash now; null when it couldn't be asked.</param>
    /// <param name="isOnStorageThatIsThere">As for <see cref="Settle"/>.</param>
    /// <param name="someStorageIsAway">As for <see cref="Settle"/>.</param>
    /// <param name="seesWholeLibrary">As for <see cref="Settle"/>.</param>
    public static RestoreOutcome SettleRestore(
        bool? inLibrary,
        bool? inSystemTrash,
        bool? isOnStorageThatIsThere,
        bool someStorageIsAway,
        bool seesWholeLibrary)
    {
        if (inLibrary == true)
            return RestoreOutcome.Restored;
        if (inLibrary is null || inSystemTrash is null or true)
            return RestoreOutcome.NotRestored;

        return !seesWholeLibrary || IsOutOfReach(isOnStorageThatIsThere, someStorageIsAway)
            ? RestoreOutcome.Unreachable
            : RestoreOutcome.Gone;
    }

    /// <summary>
    /// Whether an item neither the library nor the trash lists may only be out of reach: its
    /// volume isn't mounted, or its volume isn't known while some volume is away.
    /// </summary>
    private static bool IsOutOfReach(bool? isOnStorageThatIsThere, bool someStorageIsAway) =>
        isOnStorageThatIsThere == false || (isOnStorageThatIsThere is null && someStorageIsAway);
}
