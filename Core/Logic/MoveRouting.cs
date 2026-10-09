using SortAndDelete.Models;

namespace SortAndDelete.Logic;

/// <summary>
/// Android's rules for where an app may put a photo or video, by relative path ("Bluetooth/").
///
/// MediaStore moves a file in place only into the top-level folders its collection allows —
/// DCIM/ and Pictures/ for images; DCIM/ and Movies/ for videos, plus Pictures/ since Android
/// 11 — and anywhere else it throws ("Primary directory Bluetooth not allowed"). Android 10 and
/// 11 compare those names case-sensitively. It does let an app create files of any type under
/// Download/ and Documents/. Every other folder is reachable only through the system folder
/// picker, which from Android 11 refuses the storage root and Download/ itself (Android 10's
/// offers both). Folder grants are ExternalStorageProvider tree document ids: "primary:Bluetooth"
/// is Bluetooth/ on internal storage, "1A2B-3C4D:DCIM" is DCIM/ on that SD card.
/// </summary>
public static class MoveRouting
{
    private static readonly string[] AnyTypeFolders = ["Download", "Documents"];
    private static readonly string[] StandardFolders = ["DCIM", "Pictures", "Movies", "Download", "Documents"];

    /// <param name="videosAllowedInPictures">Android 11+; Android 10 keeps videos to DCIM/ and Movies/.</param>
    public static MoveRoute RouteFor(string relativePath, MediaKind kind, bool videosAllowedInPictures)
    {
        var folders = Segments(Canonical(relativePath));
        if (folders.Length == 0)
            return MoveRoute.NotPossible; // the storage root

        if (IsAppFolder(relativePath))
            return MoveRoute.NotPossible;

        var top = folders[0];
        if (InPlaceFolders(kind, videosAllowedInPictures).Contains(top, StringComparer.Ordinal))
            return MoveRoute.InPlace;
        if (AnyTypeFolders.Contains(top, StringComparer.Ordinal))
            return MoveRoute.Copy;
        return MoveRoute.FolderAccess;
    }

    /// <summary>
    /// Android/ belongs to apps: Android/data and obb are private, and an app's Android/media
    /// folder is deleted with the app, so a photo moved there could vanish on an uninstall.
    /// </summary>
    public static bool IsAppFolder(string relativePath) =>
        Segments(relativePath) is [var top, ..] && top.Equals("Android", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The relative path with a standard top-level folder in its standard spelling ("pictures/x/"
    /// becomes "Pictures/x/"). Shared storage ignores case, so it is the same folder, but Android
    /// 10 and 11 check the top-level name case-sensitively when moving.
    /// </summary>
    public static string Canonical(string relativePath)
    {
        var folders = Segments(relativePath);
        if (folders.Length == 0)
            return relativePath;

        var standard = StandardFolders.FirstOrDefault(name => name.Equals(folders[0], StringComparison.OrdinalIgnoreCase));
        if (standard is null || standard == folders[0])
            return relativePath;

        folders[0] = standard;
        return string.Join('/', folders) + "/";
    }

    /// <summary>The top-level folders an item of <paramref name="kind"/> goes into without asking for access.</summary>
    public static IReadOnlyList<string> FoldersWithoutAsking(MediaKind kind, bool videosAllowedInPictures) =>
        [.. InPlaceFolders(kind, videosAllowedInPictures), .. AnyTypeFolders];

    /// <summary>The top-level folders MediaStore moves an item of <paramref name="kind"/> into in place, without a copy.</summary>
    public static IReadOnlyList<string> InPlaceFoldersFor(MediaKind kind, bool videosAllowedInPictures) =>
        InPlaceFolders(kind, videosAllowedInPictures);

    /// <summary>Folder names as a phrase: "DCIM and Pictures", "DCIM, Movies and Pictures".</summary>
    public static string InWords(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    /// <summary>Where a new album for an item of <paramref name="kind"/> goes, so the move into it is in place.</summary>
    public static string NewAlbumRoot(MediaKind kind, bool videosAllowedInPictures) =>
        kind == MediaKind.Video && !videosAllowedInPictures ? "Movies" : "Pictures";

    private static string[] InPlaceFolders(MediaKind kind, bool videosAllowedInPictures) => kind switch
    {
        MediaKind.Video when videosAllowedInPictures => ["DCIM", "Movies", "Pictures"],
        MediaKind.Video => ["DCIM", "Movies"],
        _ => ["DCIM", "Pictures"],
    };

    /// <summary>
    /// Whether two relative paths name the same folder. Shared storage ignores case, so
    /// "Bluetooth/" and "bluetooth/" are one folder on disk.
    /// </summary>
    public static bool IsSameFolder(string relativePath, string otherRelativePath) =>
        Segments(relativePath).SequenceEqual(Segments(otherRelativePath), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a folder grant covers the folder at <paramref name="relativePath"/>: that folder
    /// itself or one above it. The volume isn't compared, because MediaStore albums are merged
    /// across volumes by relative path.
    /// </summary>
    public static bool GrantCovers(string treeDocumentId, string relativePath)
    {
        if (!TrySplitDocumentId(treeDocumentId, out _, out var treePath))
            return false;

        var granted = Segments(treePath);
        var wanted = Segments(relativePath);
        if (granted.Length > wanted.Length)
            return false;

        for (int i = 0; i < granted.Length; i++)
        {
            if (!granted[i].Equals(wanted[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    /// <summary>
    /// The document id of the folder at <paramref name="relativePath"/> inside a grant that
    /// covers it ("primary:Bluetooth" and "Bluetooth/2024/" give "primary:Bluetooth/2024"), or
    /// null when the grant doesn't cover it. The covered part keeps the grant's own spelling:
    /// the provider checks that a document lies inside the tree by comparing paths as written.
    /// </summary>
    public static string? FolderDocumentId(string treeDocumentId, string relativePath)
    {
        if (!GrantCovers(treeDocumentId, relativePath) ||
            !TrySplitDocumentId(treeDocumentId, out var volume, out var treePath))
            return null;

        var below = Segments(relativePath).Skip(Segments(treePath).Length);
        var path = string.Join('/', Segments(treePath).Concat(below));
        return $"{volume}:{path}";
    }

    /// <summary>Whether a folder grant is for exactly the folder at <paramref name="relativePath"/>.</summary>
    public static bool IsGrantFor(string treeDocumentId, string relativePath) =>
        TrySplitDocumentId(treeDocumentId, out _, out var treePath) && IsSameFolder(treePath, relativePath);

    /// <summary>Whether a folder grant is for an app folder (see <see cref="IsAppFolder"/>).</summary>
    public static bool IsAppFolderGrant(string treeDocumentId) =>
        TrySplitDocumentId(treeDocumentId, out _, out var treePath) && IsAppFolder(treePath);

    /// <summary>
    /// The relative path of the folder a grant is for: "primary:Pictures/Trips" gives
    /// "Pictures/Trips/", and the top of a volume ("primary:", "1A2B-3C4D:") gives "".
    /// </summary>
    public static string RelativePathOfGrant(string treeDocumentId) =>
        TrySplitDocumentId(treeDocumentId, out _, out var treePath) && Segments(treePath) is { Length: > 0 } folders
            ? string.Join('/', folders) + "/"
            : "";

    /// <summary>
    /// Whether a grant's volume ("primary", or an SD card's "1A2B-3C4D") is the MediaStore volume
    /// an item is on ("external_primary", or that SD card's "1a2b-3c4d").
    /// </summary>
    public static bool IsSameVolume(string treeDocumentId, string mediaStoreVolume)
    {
        if (!TrySplitDocumentId(treeDocumentId, out var volume, out _))
            return false;

        return volume.Equals(InternalStorageVolume, StringComparison.OrdinalIgnoreCase)
            ? mediaStoreVolume is "external_primary" or "external"
            : volume.Equals(mediaStoreVolume, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// How an item moves into a folder picked in the system folder browser, by the folder's grant
    /// (its tree document id) and where the item is now: its MediaStore volume and relative path.
    /// The folder gets the gallery's rules: never the top of a volume or an app's folder; in place
    /// where MediaStore moves the item there (only within its own volume, and never out of an app's
    /// folder); a MediaStore copy where MediaStore takes a new file on internal storage; anywhere
    /// else a copy through the folder grant.
    /// </summary>
    public static BrowsedRoute RouteForBrowsed(string treeDocumentId, string sourceVolume, string sourceRelativePath,
        MediaKind kind, bool videosAllowedInPictures)
    {
        if (!TrySplitDocumentId(treeDocumentId, out var volume, out _))
            return BrowsedRoute.NotPossible;

        var relativePath = RelativePathOfGrant(treeDocumentId);
        if (relativePath.Length == 0 || IsAppFolder(relativePath))
            return BrowsedRoute.NotPossible;

        bool sameVolume = IsSameVolume(treeDocumentId, sourceVolume);
        if (sameVolume && IsSameFolder(sourceRelativePath, relativePath))
            return BrowsedRoute.AlreadyThere;

        var route = RouteFor(relativePath, kind, videosAllowedInPictures);
        if (route == MoveRoute.InPlace && sameVolume && !IsAppFolder(sourceRelativePath))
            return BrowsedRoute.InPlace;

        if (volume.Equals(InternalStorageVolume, StringComparison.OrdinalIgnoreCase))
        {
            if (route == MoveRoute.InPlace)
                return BrowsedRoute.MediaCopy;
            if (route == MoveRoute.Copy)
                return BrowsedRoute.FilesCopy;
        }
        return BrowsedRoute.FolderCopy;
    }

    /// <summary>The volume of internal storage in a tree document id ("primary:DCIM").</summary>
    private const string InternalStorageVolume = "primary";

    private static bool TrySplitDocumentId(string documentId, out string volume, out string path)
    {
        int colon = documentId.IndexOf(':');
        volume = colon > 0 ? documentId[..colon] : "";
        path = colon > 0 ? documentId[(colon + 1)..] : "";
        return colon > 0;
    }

    private static string[] Segments(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries);
}
