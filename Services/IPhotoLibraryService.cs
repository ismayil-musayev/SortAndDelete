using SortAndDelete.Models;

namespace SortAndDelete.Services;

/// <summary>Platform abstraction over the device photo library (MediaStore on Android, Photos on iOS).</summary>
public interface IPhotoLibraryService
{
    /// <summary>
    /// True when the platform trash keeps items ~30 days before purging
    /// (Android 11+ system trash, iOS "Recently Deleted"). False on Android 10,
    /// where emptying the bin deletes permanently.
    /// </summary>
    bool SupportsSystemTrash { get; }

    /// <summary>
    /// True when photos this app moved to the system trash can be restored from inside the
    /// app (Android 11+). iOS requires the Photos app → Recently Deleted.
    /// </summary>
    bool CanRestoreFromSystemTrash { get; }

    /// <summary>Requests read/write access to the photo library. Returns true when usable (full or partial access).</summary>
    Task<bool> RequestAccessAsync();

    /// <summary>All photos and videos in the library, newest first. Excludes items already in the system trash.</summary>
    Task<IReadOnlyList<PhotoAsset>> GetAllPhotosAsync();

    /// <summary>Decoded image bytes scaled to at most <paramref name="maxPixelSize"/> on the long edge.</summary>
    Task<Stream?> GetImageStreamAsync(string photoId, int maxPixelSize, CancellationToken ct = default);

    /// <summary>File size in bytes, 0 when unavailable.</summary>
    Task<long> GetFileSizeAsync(string photoId);

    /// <summary>
    /// Moves photos to the system trash (Android 11+) or "Recently Deleted" (iOS), where the OS
    /// purges them after ~30 days. Shows the platform consent dialog. Returns true when confirmed.
    /// </summary>
    Task<bool> MoveToSystemTrashAsync(IReadOnlyList<string> photoIds);

    /// <summary>Restores items out of the system trash (Android 11+ only, see <see cref="CanRestoreFromSystemTrash"/>).</summary>
    Task<bool> RestoreFromSystemTrashAsync(IReadOnlyList<string> photoIds);

    /// <summary>
    /// Photo ids belonging to a folder/album (<paramref name="folderId"/> is <see cref="AlbumInfo.Id"/>:
    /// a relative path on Android, a collection identifier on iOS). Used by the folder filter.
    /// </summary>
    Task<IReadOnlyCollection<string>> GetAlbumMemberIdsAsync(string folderId);

    /// <summary>Existing albums/folders the user can move photos into.</summary>
    Task<IReadOnlyList<AlbumInfo>> GetAlbumsAsync();

    /// <summary>
    /// Moves the photo into the folder (Android) or adds it to the album (iOS).
    /// Returns null when the move failed; otherwise the item's id after the move —
    /// unchanged for in-place moves, a NEW id when the move had to copy the file
    /// (browsed folders, other apps' Android/media sources).
    /// </summary>
    Task<string?> MoveToAlbumAsync(string photoId, AlbumInfo album);

    /// <summary>True when items can be handed to an external viewer app (Android ACTION_VIEW).</summary>
    bool SupportsOpenExternally { get; }

    /// <summary>
    /// Opens the item with the user's default app for its type, or the system app chooser
    /// when no default is set — same behavior as tapping a file in a file manager.
    /// Returns false when no app can handle it.
    /// </summary>
    Task<bool> OpenExternallyAsync(string photoId);

    /// <summary>True when the platform offers a system folder browser (Android SAF).</summary>
    bool SupportsFolderBrowsing { get; }

    /// <summary>
    /// Opens the system folder browser so the user can pick any folder on the device
    /// (Android). The pick is persisted and the folder appears in later album lists.
    /// Returns null when cancelled or unsupported (iOS).
    /// </summary>
    Task<AlbumInfo?> PickExternalFolderAsync();

    /// <summary>Creates a new album/folder and returns it, or null on failure.</summary>
    Task<AlbumInfo?> CreateAlbumAsync(string name);
}
