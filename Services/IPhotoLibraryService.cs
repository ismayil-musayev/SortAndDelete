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

    /// <summary>
    /// Returns true when the library is usable (full or partial access), asking the user only
    /// when it is not usable yet.
    /// </summary>
    Task<bool> RequestAccessAsync();

    /// <summary>
    /// True when the user granted access to selected photos only (Android 14+), so the app sees
    /// just those until the user adds more (<see cref="SelectMorePhotosAsync"/>).
    /// </summary>
    Task<bool> HasLimitedAccessAsync();

    /// <summary>
    /// Asks once more, on the user's request, so a limited selection can grow (the Android
    /// photo picker). Returns true when the library is usable afterwards.
    /// </summary>
    Task<bool> SelectMorePhotosAsync();

    /// <summary>All photos and videos in the library, newest first. Excludes items already in the system trash.</summary>
    Task<IReadOnlyList<PhotoAsset>> GetAllPhotosAsync();

    /// <summary>Decoded image bytes scaled to at most <paramref name="maxPixelSize"/> on the long edge.</summary>
    Task<Stream?> GetImageStreamAsync(string photoId, int maxPixelSize, CancellationToken ct = default);

    /// <summary>File size in bytes, 0 when unavailable.</summary>
    Task<long> GetFileSizeAsync(string photoId);

    /// <summary>
    /// Tightly-packed RGB pixels of a square <paramref name="size"/>x<paramref name="size"/>
    /// thumbnail, for fingerprinting. Deliberately raw: the duplicate scan runs this over the
    /// whole library, and going via an encoded stream would mean a JPEG round trip per item.
    /// Null when the item cannot be decoded.
    /// </summary>
    Task<byte[]?> GetThumbnailPixelsAsync(string photoId, int size, CancellationToken ct = default);

    /// <summary>
    /// The same, for the frame <paramref name="position"/> of the way through a video (0 to 1).
    /// Costs far more than a still — only clips that share a running time with another are
    /// ever worth sampling.
    /// </summary>
    Task<byte[]?> GetVideoFramePixelsAsync(string photoId, double position, int size, CancellationToken ct = default);

    /// <summary>
    /// Hash of the file's bytes, for the exact tier. Null when the file cannot be read.
    /// Reading a whole file is expensive, so callers should only ask about items that already
    /// share a byte length with another — nothing else can be byte-identical.
    /// </summary>
    Task<string?> ComputeContentHashAsync(string photoId, CancellationToken ct = default);

    /// <summary>
    /// Moves photos to the system trash (Android 11+) or "Recently Deleted" (iOS), where the OS
    /// purges them after ~30 days. Shows the platform consent dialog. Returns true when confirmed.
    /// Android 10 has no system trash: there they are deleted for good, through the folders the
    /// user opens for the app in the system folder picker (once per folder). True means every one
    /// is gone. False means not every one is: the user backed out of the folder picker (then none
    /// is deleted), or a file couldn't be deleted (the others still are).
    /// </summary>
    Task<bool> MoveToSystemTrashAsync(IReadOnlyList<string> photoIds);

    /// <summary>Restores items out of the system trash (Android 11+ only, see <see cref="CanRestoreFromSystemTrash"/>).</summary>
    Task<bool> RestoreFromSystemTrashAsync(IReadOnlyList<string> photoIds);

    /// <summary>
    /// Photo ids belonging to a folder/album (<paramref name="folderId"/> is <see cref="AlbumInfo.Id"/>:
    /// a relative path on Android, a collection identifier on iOS). Used by the folder filter.
    /// </summary>
    Task<IReadOnlyCollection<string>> GetAlbumMemberIdsAsync(string folderId);

    /// <summary>Existing albums/folders in the library (the folder filter, and the source of move targets).</summary>
    Task<IReadOnlyList<AlbumInfo>> GetAlbumsAsync();

    /// <summary>
    /// The albums an item of <paramref name="kind"/> can be moved into. Android leaves out the
    /// folders no app may write to (and on Android 10 those a move could only copy into), and
    /// flags those it can reach only after the user grants access to the folder
    /// (<see cref="RequestFolderAccessAsync"/>).
    /// </summary>
    Task<IReadOnlyList<MoveTarget>> GetMoveTargetsAsync(MediaKind kind);

    /// <summary>
    /// Asks the user for lasting access to the album's folder through the system folder picker,
    /// opened on that folder (Android). Always granted on iOS, which never needs it.
    /// </summary>
    Task<FolderAccessResult> RequestFolderAccessAsync(AlbumInfo album);

    /// <summary>
    /// Moves the photo into the folder (Android) or adds it to the album (iOS). When moved, the
    /// result carries the item's id afterwards — unchanged for in-place moves, a NEW id when the
    /// move had to copy the file (folders MediaStore won't move into, other apps' Android/media
    /// sources). A copy is checked before the original is removed (into the system trash on
    /// Android 11+). Backing out of a system dialog or a question cancels; a failure says why.
    /// <paramref name="feedback"/> gets the progress of a large copy, and is asked when something
    /// can't be kept (a copy's date) before anything is removed.
    /// </summary>
    Task<MoveResult> MoveToAlbumAsync(string photoId, AlbumInfo album, MoveFeedback? feedback = null);

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
    /// Opens the system folder browser so the user can pick a folder on the device's own storage
    /// or SD card (Android) to move an item of <paramref name="kind"/> into. The pick is persisted
    /// and the folder appears in later album lists. A folder moves can't go into (another
    /// provider's, the top of the storage, an app's, and on Android 10 one that would need a
    /// copy) comes back as a problem to show instead. <see cref="FolderPick.None"/> when cancelled
    /// or unsupported (iOS).
    /// </summary>
    Task<FolderPick> PickExternalFolderAsync(MediaKind kind);

    /// <summary>
    /// Creates a new album/folder for an item of <paramref name="kind"/> and returns it, or null
    /// on failure. On Android it lands where moving that item into it needs no folder access.
    /// </summary>
    Task<AlbumInfo?> CreateAlbumAsync(string name, MediaKind kind);
}
