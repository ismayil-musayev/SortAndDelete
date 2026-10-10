namespace SortAndDelete.Models;

/// <summary>
/// What emptying the bin did with the items it was asked to remove, each in exactly one of four
/// sets. Only <see cref="Removed"/> is ever recorded as removed and counted as freed: an item is
/// there when it is out of the library and it can be said where it went.
/// </summary>
/// <param name="Removed">
/// Out of the library and accounted for: in the system trash (Android 11+, iOS Recently Deleted),
/// or taken out by this request itself, which found them there first (deleted for good with the
/// file checked gone on Android 10, trashed with the user's consent on Android 11+).
/// </param>
/// <param name="Missing">
/// The library no longer has them, on storage that is there and with the whole library in view,
/// and they aren't in the system trash: something else removed, renamed or hid them. Nothing
/// says their files are gone, so they are not recorded as removed; their decisions are forgotten
/// instead, and one that turns up again comes back unreviewed.
/// </param>
/// <param name="Unreachable">
/// Out of reach now: on storage that isn't there (an SD card taken out, or one it can't be told
/// about), or outside the photos the app has been given access to. They were not touched, and
/// stay in the bin until they can be reached again.
/// </param>
/// <param name="Left">
/// Still in the library, or it couldn't be asked: the user said no, Android refused, or the
/// request failed. They stay in the bin.
/// </param>
public sealed record TrashResult(
    IReadOnlySet<string> Removed,
    IReadOnlySet<string> Missing,
    IReadOnlySet<string> Unreachable,
    IReadOnlySet<string> Left)
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    /// <summary>Nothing was asked for.</summary>
    public static TrashResult Nothing { get; } = new(None, None, None, None);

    /// <summary>Every item went where the request sent it.</summary>
    public static TrashResult AllRemoved(IEnumerable<string> photoIds) =>
        new(photoIds.ToHashSet(StringComparer.Ordinal), None, None, None);

    /// <summary>The request did nothing: every item is where it was.</summary>
    public static TrashResult AllLeft(IEnumerable<string> photoIds) =>
        new(None, None, None, photoIds.ToHashSet(StringComparer.Ordinal));

    /// <summary>Whether the bin is empty afterwards: nothing stays in it.</summary>
    public bool EmptiesBin => Unreachable.Count == 0 && Left.Count == 0;
}

/// <summary>What became of an item the user asked to have back out of the system trash.</summary>
public enum RestoreOutcome
{
    /// <summary>It is back in the library.</summary>
    Restored,

    /// <summary>It is still in the trash (the user said no, or Android refused), or it couldn't be asked.</summary>
    NotRestored,

    /// <summary>Neither in the library nor in the trash, on storage that is there: purged, or removed elsewhere.</summary>
    Gone,

    /// <summary>
    /// Out of reach now (on an SD card taken out, or outside the photos the app may see): it was
    /// not touched, and can be restored once it can be reached again.
    /// </summary>
    Unreachable,
}
