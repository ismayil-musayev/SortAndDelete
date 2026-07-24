using SortAndDelete.Logic;
using SortAndDelete.Models;

namespace SortAndDelete.Services;

/// <summary>
/// Combines the platform photo library with the local review store:
/// caches the photo list, builds month groups and deck queues, resolves folder filters.
/// </summary>
public sealed class GalleryService(IPhotoLibraryService library, ReviewStore store)
{
    /// <summary>Pseudo-folder id for the system-wide favorites flag (Samsung/Google "Favourites").</summary>
    public const string FavoritesFolderId = "sortanddelete:favorites";

    private List<PhotoAsset>? _photos;
    private Dictionary<string, PhotoAsset>? _byId;
    private readonly Dictionary<string, IReadOnlySet<string>> _folderMembers = [];

    public IPhotoLibraryService Library => library;
    public ReviewStore Store => store;

    public async Task<IReadOnlyList<PhotoAsset>> GetPhotosAsync(bool refresh = false)
    {
        if (_photos is null || refresh)
        {
            var items = await library.GetAllPhotosAsync();
            _photos = [.. items];
            _byId = _photos.ToDictionary(p => p.Id);
            _folderMembers.Clear();
        }
        return _photos;
    }

    /// <summary>Call after photos were deleted/moved outside the cache's knowledge.</summary>
    public void Invalidate()
    {
        _photos = null;
        _byId = null;
        _folderMembers.Clear();
    }

    public PhotoAsset? Find(string photoId) =>
        _byId is not null && _byId.TryGetValue(photoId, out var photo) ? photo : null;

    /// <summary>
    /// Photos visible under a folder filter. On Android the folder id is a relative path we
    /// already carry on each photo; on iOS it's an album whose membership we fetch once.
    /// </summary>
    public async Task<IReadOnlyList<PhotoAsset>> GetPhotosAsync(string? folderId)
    {
        var photos = await GetPhotosAsync();
        if (string.IsNullOrEmpty(folderId))
            return photos;

        if (folderId == FavoritesFolderId)
            return photos.Where(p => p.IsFavorite).ToList();

        if (!_folderMembers.TryGetValue(folderId, out var members))
        {
            var direct = photos.Where(p => p.FolderPath == folderId).Select(p => p.Id).ToHashSet();
            if (direct.Count == 0)
                direct = (await library.GetAlbumMemberIdsAsync(folderId)).ToHashSet();
            _folderMembers[folderId] = members = direct;
        }

        return photos.Where(p => members.Contains(p.Id)).ToList();
    }

    public async Task<List<MonthGroup>> GetMonthGroupsAsync(bool refresh = false, string? folderId = null)
    {
        if (refresh)
            await GetPhotosAsync(refresh: true);
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
