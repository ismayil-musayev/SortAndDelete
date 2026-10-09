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

    private List<PhotoAsset>? _photos;
    private Dictionary<string, PhotoAsset>? _byId;
    private IReadOnlyList<AlbumInfo>? _albums;
    private readonly Dictionary<string, IReadOnlySet<string>> _folderMembers = new(FolderFilter.IdComparer);

    // Bumped whenever the library may have changed. The lists are current while they were
    // loaded at the latest version.
    private int _version;
    private int _loadedVersion = -1;
    private Task? _loading;

    public IPhotoLibraryService Library => library;
    public ReviewStore Store => store;

    /// <summary>
    /// Every photo and video, loaded together with <see cref="GetAlbumsAsync"/> and kept until
    /// <see cref="Invalidate"/> or <see cref="MarkStale"/>.
    /// </summary>
    public async Task<IReadOnlyList<PhotoAsset>> GetPhotosAsync()
    {
        await EnsureLoadedAsync();
        return _photos!;
    }

    /// <summary>
    /// The albums the folder filter shows, from the same load as <see cref="GetPhotosAsync()"/>: on
    /// Android the folders counted from the photos themselves (<see cref="FolderFilter.FoldersOf"/>),
    /// so their counts match the photos; elsewhere the library's albums, with the counts it gives,
    /// loaded alongside. Move targets come from the library itself: they need empty and browsed
    /// folders as they are now.
    /// </summary>
    public async Task<IReadOnlyList<AlbumInfo>> GetAlbumsAsync()
    {
        await EnsureLoadedAsync();
        return _albums!;
    }

    /// <summary>Call after photos were deleted/moved outside the cache's knowledge.</summary>
    public void Invalidate()
    {
        _photos = null;
        _byId = null;
        _albums = null;
        _folderMembers.Clear();
        _version++;
    }

    /// <summary>
    /// Call when other apps may have changed the library, as while the app was in the background:
    /// the next read loads it again. Until then <see cref="Find"/> answers from the list as loaded.
    /// </summary>
    public void MarkStale() => _version++;

    /// <summary>
    /// Loads photos and albums together when they are missing or older than the latest version.
    /// One load runs at a time, and a change marked while it runs means another load after it.
    /// </summary>
    private async Task EnsureLoadedAsync()
    {
        while (_photos is null || _loadedVersion != _version)
        {
            var loading = _loading ??= LoadAsync(_version);
            try
            {
                await loading;
            }
            finally
            {
                if (_loading == loading)
                    _loading = null;
            }
        }
    }

    private async Task LoadAsync(int version)
    {
#if ANDROID
        // Every item carries its folder's relative path, so the photos give every folder with
        // something in it. The library's album list also walks the folders on disk for empty
        // ones, which the chips skip, and that walk can take longer than the photo load.
        var photos = await library.GetAllPhotosAsync();
        var albums = FolderFilter.FoldersOf(photos);
#else
        var loadingPhotos = library.GetAllPhotosAsync();
        var loadingAlbums = library.GetAlbumsAsync();
        await Task.WhenAll(loadingPhotos, loadingAlbums);
        var photos = await loadingPhotos;
        var albums = await loadingAlbums;
#endif

        _photos = [.. photos];
        _byId = _photos.ToDictionary(p => p.Id);
        _albums = albums;
        _folderMembers.Clear();
        _loadedVersion = version;
    }

    public PhotoAsset? Find(string photoId) =>
        _byId is not null && _byId.TryGetValue(photoId, out var photo) ? photo : null;

    /// <summary>
    /// Photos visible under a folder filter. On Android the folder id is a relative path we
    /// already carry on each photo, matched by <see cref="FolderFilter.ItemsIn"/>, so a folder's
    /// months count what its chip counts; on iOS it's an album whose membership we fetch once.
    /// </summary>
    public async Task<IReadOnlyList<PhotoAsset>> GetPhotosAsync(string? folderId)
    {
        var photos = await GetPhotosAsync();
        if (string.IsNullOrEmpty(folderId))
            return photos;

        if (FolderFilter.IdComparer.Equals(folderId, FavoritesFolderId))
            return photos.Where(p => p.IsFavorite).ToList();

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
        return MonthAggregator.Build(photos, reviewedIds);
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
