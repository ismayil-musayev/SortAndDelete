namespace SortAndDelete.Models;

/// <summary>How an item gets into a folder, which decides what Android asks the user on the way.</summary>
public enum MoveRoute
{
    /// <summary>MediaStore moves the file itself: same item, every detail kept (iOS albums too).</summary>
    InPlace,

    /// <summary>
    /// MediaStore will not move the file there but lets the app create a copy there, which the
    /// app owns. The copy is a new item; the original is deleted after it.
    /// </summary>
    Copy,

    /// <summary>
    /// Only reachable through the system folder picker: the user grants the folder once, then
    /// the item is copied in and the original deleted.
    /// </summary>
    FolderAccess,

    /// <summary>No app may put files there, by any route.</summary>
    NotPossible,
}

/// <summary>How an item gets into a folder picked in the system folder browser (Android).</summary>
public enum BrowsedRoute
{
    /// <summary>The top of a volume, or an app's folder: refused, as the same folders are in the gallery.</summary>
    NotPossible,

    /// <summary>The item is in that folder already: nothing to move.</summary>
    AlreadyThere,

    /// <summary>MediaStore moves the item itself (same volume, a folder it allows): same item, every detail kept.</summary>
    InPlace,

    /// <summary>
    /// Internal storage, under a folder MediaStore moves the item into but not from where it is
    /// (another volume, an app's folder): MediaStore copies it into its own collection.
    /// </summary>
    MediaCopy,

    /// <summary>Internal storage, under Download/ or Documents/: MediaStore copies it in as a file.</summary>
    FilesCopy,

    /// <summary>Anywhere else: copied in through the folder grant.</summary>
    FolderCopy,
}

/// <summary>An album offered as a move target, and whether Android first asks for access to its folder.</summary>
public sealed record MoveTarget(AlbumInfo Album, bool NeedsFolderAccess);

/// <summary>What the system folder browser gave back: a folder to move into, or why it can't be one.</summary>
public sealed record FolderPick(AlbumInfo? Album, string? Problem = null)
{
    /// <summary>The user backed out, or the platform has no folder browser.</summary>
    public static FolderPick None { get; } = new(Album: null);
}

/// <summary>How a request for access to an album's folder ended.</summary>
public enum FolderAccessResult
{
    Granted,

    /// <summary>The user backed out of the system folder picker.</summary>
    Cancelled,

    /// <summary>The user granted a folder, but not one that holds the album.</summary>
    OtherFolder,
}
