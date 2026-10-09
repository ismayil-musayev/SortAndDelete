namespace SortAndDelete.Logic;

/// <summary>
/// Photos and videos as documents of the device's storage provider (internal storage and SD
/// cards). On Android 10 the app deletes through it: MediaStore there drops another app's item
/// but keeps its file. The provider deletes only inside a folder the user granted the app.
/// Document ids name the volume, then the path on it: "primary:DCIM/Camera/a.jpg" is
/// DCIM/Camera/a.jpg on internal storage, "1103-3D09:DCIM/a.jpg" is DCIM/a.jpg on the SD card
/// mounted at /storage/1103-3D09.
/// </summary>
public static class StorageDocuments
{
    /// <summary>Where SD cards are mounted, each under its file-system UUID ("1103-3D09").</summary>
    private const string StorageRoot = "/storage/";

    /// <summary>
    /// Links to internal storage as mounted for the app's user. Android 10 keeps the path a
    /// legacy app gave a file as given, so MediaStore can list a file under one of them.
    /// </summary>
    private static readonly string[] InternalStorageLinks = ["/sdcard/", "/storage/self/primary/"];

    /// <summary>
    /// The document id of the file at <paramref name="path"/>: on internal storage under
    /// <paramref name="internalStorageRoot"/> ("/storage/emulated/0") or a link to it a legacy
    /// app may have stored (/sdcard/, /storage/self/primary/), on an SD card under
    /// /storage/1103-3D09/, whose name the id keeps as mounted (the provider's volume names are
    /// case-sensitive). Null anywhere else, for the top of a volume, and for a path that steps
    /// out of a folder ("..").
    /// </summary>
    public static string? DocumentIdOfPath(string path, string internalStorageRoot)
    {
        var internalRoot = internalStorageRoot.TrimEnd('/') + "/";
        if (path.StartsWith(internalRoot, StringComparison.Ordinal))
            return DocumentId(MoveRouting.InternalStorageVolume, path[internalRoot.Length..]);

        foreach (var link in InternalStorageLinks)
        {
            if (path.StartsWith(link, StringComparison.Ordinal))
                return DocumentId(MoveRouting.InternalStorageVolume, path[link.Length..]);
        }

        if (!path.StartsWith(StorageRoot, StringComparison.Ordinal))
            return null;

        var onStorage = path[StorageRoot.Length..];
        int slash = onStorage.IndexOf('/');
        if (slash <= 0)
            return null;

        // /storage/emulated holds every user's internal storage, and /storage/self only the link above.
        var volume = onStorage[..slash];
        return volume is "emulated" or "self" ? null : DocumentId(volume, onStorage[(slash + 1)..]);
    }

    /// <summary>
    /// The folder to ask access to for deleting a document: its top-level folder on its volume
    /// ("primary:DCIM/Camera/a.jpg" gives "primary:DCIM"), so one grant covers everything inside
    /// it, DCIM/Camera and DCIM/Screenshots alike. Under Android/, which holds every app's own
    /// folders, it is the document's own folder; for a file at the top of the volume, the top
    /// itself ("primary:"). Null when <paramref name="documentId"/> names no file.
    /// </summary>
    public static string? FolderToGrant(string documentId)
    {
        if (!MoveRouting.TrySplitDocumentId(documentId, out var volume, out var path) ||
            MoveRouting.Segments(path) is not { Length: > 0 } segments)
            return null;

        var folders = segments[..^1];
        if (folders.Length > 1 && !MoveRouting.IsAppFolder(path))
            folders = folders[..1];
        return $"{volume}:{string.Join('/', folders)}";
    }

    /// <summary>
    /// The folder to ask access to first for deleting <paramref name="documentIds"/>: of the
    /// folders <see cref="FolderToGrant"/> names for them, the one nearest the top of its volume
    /// (the first of those as near), so that one grant covers the most. Null when it names none.
    /// </summary>
    public static string? FirstFolderToGrant(IEnumerable<string> documentIds) =>
        documentIds.Select(FolderToGrant).OfType<string>().MinBy(Depth);

    /// <summary>
    /// The id of a document inside a folder grant (a tree document id), or null when the grant
    /// doesn't cover the document's folder, that folder itself or one above it, on the same
    /// volume. Volumes and folders compare ignoring case. What the grant covers keeps the grant's
    /// own spelling, which is the provider's: it checks that a document lies inside the tree by
    /// comparing paths as written (see <see cref="MoveRouting.FolderDocumentId"/>). The rest is
    /// the document's.
    /// </summary>
    public static string? DocumentIdInGrant(string treeDocumentId, string documentId)
    {
        if (!MoveRouting.TrySplitDocumentId(treeDocumentId, out var grantVolume, out _) ||
            !MoveRouting.TrySplitDocumentId(documentId, out var volume, out var path) ||
            !grantVolume.Equals(volume, StringComparison.OrdinalIgnoreCase) ||
            MoveRouting.Segments(path) is not { Length: > 0 } segments)
            return null;

        var folder = string.Join('/', segments[..^1]) + "/";
        return MoveRouting.GrantCovers(treeDocumentId, folder)
            ? MoveRouting.FolderDocumentId(treeDocumentId, path)
            : null;
    }

    /// <summary>How many folders down a folder lies on its volume: 0 for "primary:", 1 for "primary:DCIM".</summary>
    private static int Depth(string folderDocumentId) =>
        MoveRouting.TrySplitDocumentId(folderDocumentId, out _, out var path) ? MoveRouting.Segments(path).Length : 0;

    private static string? DocumentId(string volume, string relativePath)
    {
        var segments = MoveRouting.Segments(relativePath);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
            return null;
        return $"{volume}:{string.Join('/', segments)}";
    }
}
