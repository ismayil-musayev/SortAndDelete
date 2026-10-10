using SortAndDelete.Logic;
using SortAndDelete.Models;

namespace SortAndDelete.Services;

/// <summary>
/// Combines the platform photo library with the local review store:
/// caches the photo and album lists, builds month groups and deck queues, resolves folder filters.
/// </summary>
public sealed class GalleryService(IPhotoLibraryService library, ReviewStore store)
{
    /// <summary>Pseudo-folder id for the system-wide favorites flag (Samsung/Google "Favourites").</summary>
    public const string FavoritesFolderId = FolderFilter.FavoritesFolderId;

    // The photo and album lists as the latest load read them. Reads, marks and invalidations all
    // happen on the UI thread, and a load hands its snapshot back there (see VersionedLoad).
    private readonly VersionedLoad<LibrarySnapshot> _snapshot = new(() => LoadAsync(library));

    // The members of the folders filtered by so far, in the snapshot they were taken from.
    private readonly Dictionary<string, IReadOnlySet<string>> _folderMembers = new(FolderFilter.IdComparer);
    private LibrarySnapshot? _membersOf;

    public IPhotoLibraryService Library => library;
    public ReviewStore Store => store;

    /// <summary>
    /// When the latest load began, on <see cref="ChangeDebounce.Now"/>; null before the first. The
    /// load reads the library as it is then or later.
    /// </summary>
    public TimeSpan? LoadStartedAt => _snapshot.LoadStartedAt;

    /// <summary>
    /// Every photo and video, loaded together with <see cref="GetAlbumsAsync"/> and kept until
    /// <see cref="Invalidate"/> or <see cref="MarkStale"/>.
    /// </summary>
    public async Task<IReadOnlyList<PhotoAsset>> GetPhotosAsync() => (await _snapshot.GetAsync()).Photos;

    /// <summary>
    /// The albums the folder filter shows, from the same load as <see cref="GetPhotosAsync()"/>: on
    /// Android the folders counted from the photos themselves (<see cref="FolderFilter.FoldersOf"/>),
    /// so their counts match the photos; elsewhere the library's albums, with the counts it gives,
    /// loaded alongside. Move targets come from the library itself: they need empty and browsed
    /// folders as they are now.
    /// </summary>
    public async Task<IReadOnlyList<AlbumInfo>> GetAlbumsAsync() => (await _snapshot.GetAsync()).Albums;

    /// <summary>
    /// Call after photos were deleted/moved outside the cache's knowledge, also when the request
    /// failed: it can have changed some. The lists kept are dropped, and a read that begins after
    /// this waits for a load that began after it, so it sees what the change left (the bin counts
    /// on that to tell what a failed request still removed).
    /// </summary>
    public void Invalidate() => _snapshot.Invalidate();

    /// <summary>
    /// Call when other apps may have changed the library, as while the app was in the background:
    /// the reads from now on load it again, while a read already waiting keeps the load it waits
    /// for. Until a load ends, <see cref="Find"/> answers from the list as loaded.
    /// </summary>
    public void MarkStale() => _snapshot.MarkStale();

    private static Task<LibrarySnapshot> LoadAsync(IPhotoLibraryService library) =>
        // The query and everything made from it run on the pool, so a big library doesn't hold up
        // the UI thread.
        Task.Run(async () =>
        {
#if ANDROID
            // Every item carries its folder's relative path, so the photos give every folder with
            // something in it. The library's album list also walks the folders on disk for empty
            // ones, which the chips skip, and that walk can take longer than the photo load.
            return new LibrarySnapshot(await library.GetAllPhotosAsync(), FolderFilter.FoldersOf);
#else
            var loadingPhotos = library.GetAllPhotosAsync();
            var loadingAlbums = library.GetAlbumsAsync();
            await Task.WhenAll(loadingPhotos, loadingAlbums);
            var albums = await loadingAlbums;
            return new LibrarySnapshot(await loadingPhotos, _ => albums);
#endif
        });

    public PhotoAsset? Find(string photoId) => _snapshot.Value?.Find(photoId);

    /// <summary>
    /// Photos visible under a folder filter. On Android the folder id is a relative path we
    /// already carry on each photo, matched by <see cref="FolderFilter.ItemsIn"/>, so a folder's
    /// months count what its chip counts; on iOS it's an album whose membership we fetch once.
    /// </summary>
    public async Task<IReadOnlyList<PhotoAsset>> GetPhotosAsync(string? folderId)
    {
        var snapshot = await _snapshot.GetAsync();
        var photos = snapshot.Photos;
        if (string.IsNullOrEmpty(folderId))
            return photos;

        if (FolderFilter.IdComparer.Equals(folderId, FavoritesFolderId))
            return photos.Where(p => p.IsFavorite).ToList();

        if (_membersOf != snapshot)
        {
            _folderMembers.Clear();
            _membersOf = snapshot;
        }

        if (!_folderMembers.TryGetValue(folderId, out var members))
        {
            var direct = FolderFilter.ItemsIn(photos, folderId).Select(p => p.Id).ToHashSet();
            if (direct.Count == 0)
                direct = (await library.GetAlbumMemberIdsAsync(folderId)).ToHashSet();
            _folderMembers[folderId] = members = direct;
        }

        return photos.Where(p => members.Contains(p.Id)).ToList();
    }

    public async Task<List<MonthGroup>> GetMonthGroupsAsync(string? folderId = null)
    {
        var photos = await GetPhotosAsync(folderId);
        var reviewedIds = await GetReviewedIdsAsync();

        // Built on the pool: nothing changes these photos meanwhile (a load replaces the list
        // rather than changing it, and the sizes iOS fills in later aren't read here).
        return await Task.Run(() => MonthAggregator.Build(photos, reviewedIds));
    }

    /// <summary>Builds the queue for a deck. Reviewed photos are skipped — resume for free.</summary>
    public async Task<List<PhotoAsset>> GetQueueAsync(DeckMode mode, string? monthKey = null, string? folderId = null)
    {
        var photos = await GetPhotosAsync(folderId);
        var reviewedIds = await GetReviewedIdsAsync();

        switch (mode)
        {
            case DeckMode.Month:
                return QueueBuilder.ForMonth(photos, reviewedIds, monthKey ?? "");

            case DeckMode.Biggest:
                // iOS doesn't report sizes up front — fill the gaps before sorting.
                var unreviewed = photos.Where(p => !reviewedIds.Contains(p.Id)).ToList();
                await EnsureSizesAsync(unreviewed);
                return QueueBuilder.Biggest(unreviewed, reviewedIds);

            default:
                return [];
        }
    }

    /// <summary>Fills missing SizeBytes (iOS) with a small parallel fan-out.</summary>
    public async Task EnsureSizesAsync(IEnumerable<PhotoAsset> photos)
    {
        var missing = photos.Where(p => p.SizeBytes == 0).ToList();
        if (missing.Count == 0)
            return;

        using var gate = new SemaphoreSlim(8);
        var tasks = missing.Select(async photo =>
        {
            await gate.WaitAsync();
            try
            {
                photo.SizeBytes = await library.GetFileSizeAsync(photo.Id);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
    }

    public async Task<IReadOnlySet<string>> GetReviewedIdsAsync()
    {
        var reviews = await store.GetAllAsync();
        return reviews.Select(r => r.PhotoId).ToHashSet();
    }

    /// <summary>Lazy image source for a photo; the stream is only opened when the Image control needs it.</summary>
    public ImageSource CreateThumbnail(string photoId, int maxPixelSize) =>
        ImageSource.FromStream(async ct =>
            await library.GetImageStreamAsync(photoId, maxPixelSize, ct) ?? Stream.Null);
}
