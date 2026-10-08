using System.Security.Cryptography;
using AVFoundation;
using CoreGraphics;
using CoreMedia;
using Foundation;
using Photos;
using SortAndDelete.Models;

namespace SortAndDelete.Services;

/// <summary>Photos-framework-backed library for iOS 15+.</summary>
public sealed class IosPhotoLibraryService : IPhotoLibraryService
{
    /// <summary>Deleted assets land in "Recently Deleted" and are purged by iOS after ~30 days.</summary>
    public bool SupportsSystemTrash => true;

    /// <summary>Apple does not allow apps to restore from Recently Deleted — only the Photos app can.</summary>
    public bool CanRestoreFromSystemTrash => false;

    public async Task<bool> RequestAccessAsync()
    {
        var status = PHPhotoLibrary.GetAuthorizationStatus(PHAccessLevel.ReadWrite);
        if (status == PHAuthorizationStatus.NotDetermined)
            status = await PHPhotoLibrary.RequestAuthorizationAsync(PHAccessLevel.ReadWrite);

        return status is PHAuthorizationStatus.Authorized or PHAuthorizationStatus.Limited;
    }

    public Task<IReadOnlyList<PhotoAsset>> GetAllPhotosAsync() => Task.Run<IReadOnlyList<PhotoAsset>>(() =>
    {
        var list = new List<PhotoAsset>();
        AppendAssets(list, PHAssetMediaType.Image);
        AppendAssets(list, PHAssetMediaType.Video);
        list.Sort((a, b) => b.TakenAt.CompareTo(a.TakenAt)); // newest first
        return list;
    });

    private static void AppendAssets(List<PhotoAsset> list, PHAssetMediaType mediaType)
    {
        var options = new PHFetchOptions
        {
            SortDescriptors = [new NSSortDescriptor("creationDate", ascending: false)],
        };

        var result = PHAsset.FetchAssets(mediaType, options);
        for (nint i = 0; i < result.Count; i++)
        {
            if (result.ObjectAt(i) is not PHAsset asset)
                continue;

            var takenAt = asset.CreationDate is NSDate date
                ? ((DateTime)date).ToLocalTime()
                : DateTime.MinValue;

            bool isVideo = mediaType == PHAssetMediaType.Video;

            string fileName = "";
            try
            {
                // The fast path Photos itself uses; falls back to empty when unavailable.
                if (asset.ValueForKey(new NSString("filename")) is NSString name)
                    fileName = name.ToString();
            }
            catch
            {
                // cosmetic only
            }

            list.Add(new PhotoAsset
            {
                Id = asset.LocalIdentifier,
                Kind = isVideo ? MediaKind.Video : MediaKind.Image,
                TakenAt = takenAt,
                SizeBytes = 0, // looked up lazily via GetFileSizeAsync — too slow to do for every asset
                Width = (int)asset.PixelWidth,
                Height = (int)asset.PixelHeight,
                DurationMs = isVideo ? (long)(asset.Duration * 1000) : 0,
                FileName = fileName,
                FolderPath = "", // iOS has albums, not folders
                IsFavorite = asset.Favorite,
            });
        }
    }

    public Task<Stream?> GetImageStreamAsync(string photoId, int maxPixelSize, CancellationToken ct = default) =>
        Task.Run<Stream?>(() =>
        {
            var asset = FetchAsset(photoId);
            if (asset is null)
                return null;

            using var options = new PHImageRequestOptions
            {
                NetworkAccessAllowed = true, // pull from iCloud when the original is offloaded
                Synchronous = true,
                DeliveryMode = PHImageRequestOptionsDeliveryMode.HighQualityFormat,
                ResizeMode = PHImageRequestOptionsResizeMode.Fast,
            };

            Stream? stream = null;
            PHImageManager.DefaultManager.RequestImageForAsset(
                asset,
                new CGSize(maxPixelSize, maxPixelSize),
                PHImageContentMode.AspectFit,
                options,
                (image, _) =>
                {
                    if (image?.AsJPEG(0.92f) is NSData data)
                        stream = data.AsStream();
                });

            return stream;
        }, ct);

    /// <summary>Images are fetched at this size and squared down from there.</summary>
    private const int ThumbnailSourceSize = 256;

    public Task<byte[]?> GetThumbnailPixelsAsync(string photoId, int size, CancellationToken ct = default) =>
        Task.Run<byte[]?>(() =>
        {
            var asset = FetchAsset(photoId);
            if (asset is null)
                return null;

            using var options = new PHImageRequestOptions
            {
                NetworkAccessAllowed = true, // pull from iCloud when the original is offloaded
                Synchronous = true,
                DeliveryMode = PHImageRequestOptionsDeliveryMode.HighQualityFormat,
                ResizeMode = PHImageRequestOptionsResizeMode.Exact,
            };

            byte[]? pixels = null;
            PHImageManager.DefaultManager.RequestImageForAsset(
                asset,
                new CGSize(ThumbnailSourceSize, ThumbnailSourceSize),
                PHImageContentMode.AspectFill,
                options,
                (image, _) =>
                {
                    if (image?.CGImage is { } frame)
                        pixels = ToPackedRgb(frame, size);
                });
            return pixels;
        }, ct);

    public Task<byte[]?> GetVideoFramePixelsAsync(string photoId, double position, int size, CancellationToken ct = default) =>
        Task.Run<byte[]?>(() =>
        {
            var asset = FetchAsset(photoId);
            if (asset is null)
                return null;

            using var options = new PHVideoRequestOptions
            {
                NetworkAccessAllowed = true,
                DeliveryMode = PHVideoRequestOptionsDeliveryMode.HighQualityFormat,
            };

            byte[]? pixels = null;
            using var finished = new SemaphoreSlim(0, 1);

            PHImageManager.DefaultManager.RequestAvAsset(asset, options, (video, _, _) =>
            {
                try
                {
                    if (video is null)
                        return;

                    using var generator = new AVAssetImageGenerator(video)
                    {
                        AppliesPreferredTrackTransform = true,

                        // Exact, not nearest-keyframe: a re-encode lays its keyframes out
                        // differently, so snapping to one would sample a different moment in
                        // each copy and the frames would never line up.
                        RequestedTimeToleranceBefore = CMTime.Zero,
                        RequestedTimeToleranceAfter = CMTime.Zero,
                    };

                    var at = CMTime.FromSeconds(video.Duration.Seconds * Math.Clamp(position, 0, 1), 600);
                    using var frame = generator.CopyCGImageAtTime(at, out _, out var error);
                    if (frame is not null && error is null)
                        pixels = ToPackedRgb(frame, size);
                }
                catch
                {
                    // leave the frame unsampled — the clip just never matches
                }
                finally
                {
                    finished.Release();
                }
            });

            finished.Wait(TimeSpan.FromSeconds(30));
            return pixels;
        }, ct);

    public Task<string?> ComputeContentHashAsync(string photoId, CancellationToken ct = default) =>
        Task.Run<string?>(() =>
        {
            var asset = FetchAsset(photoId);
            if (asset is null)
                return null;

            var resources = PHAssetResource.GetAssetResources(asset);
            var resource = resources.FirstOrDefault(r =>
                    r.ResourceType is PHAssetResourceType.Photo or PHAssetResourceType.Video)
                ?? resources.FirstOrDefault();
            if (resource is null)
                return null;

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var finished = new SemaphoreSlim(0, 1);
            bool failed = false;

            using var options = new PHAssetResourceRequestOptions { NetworkAccessAllowed = true };
            PHAssetResourceManager.DefaultManager.RequestData(
                resource,
                options,
                data => hasher.AppendData(data.ToArray()),
                error =>
                {
                    failed = error is not null;
                    finished.Release();
                });

            if (!finished.Wait(TimeSpan.FromMinutes(5)))
                return null;

            return failed ? null : Convert.ToHexString(hasher.GetHashAndReset());
        }, ct);

    /// <summary>
    /// Squashes the image into a square and unpacks it to RGB triples.
    /// </summary>
    /// <remarks>
    /// Core Graphics draws from the bottom left, so these grids come out vertically mirrored
    /// against the Android ones. That is harmless: fingerprints are only ever compared with
    /// others from the same device, and never travel.
    /// </remarks>
    private static byte[] ToPackedRgb(CGImage image, int size)
    {
        var raw = new byte[size * size * 4];
        using var colourSpace = CGColorSpace.CreateDeviceRGB();
        using var context = new CGBitmapContext(
            raw, size, size, 8, size * 4, colourSpace, CGImageAlphaInfo.PremultipliedLast);

        context.DrawImage(new CGRect(0, 0, size, size), image);

        var rgb = new byte[size * size * 3];
        for (int i = 0; i < size * size; i++)
        {
            rgb[i * 3] = raw[i * 4];
            rgb[(i * 3) + 1] = raw[(i * 4) + 1];
            rgb[(i * 3) + 2] = raw[(i * 4) + 2];
        }
        return rgb;
    }

    /// <summary>Not possible on iOS — Recently Deleted is only accessible to the Photos app.</summary>
    public Task<bool> RestoreFromSystemTrashAsync(IReadOnlyList<string> photoIds) => Task.FromResult(false);

    public Task<IReadOnlyCollection<string>> GetAlbumMemberIdsAsync(string folderId) =>
        Task.Run<IReadOnlyCollection<string>>(() =>
        {
            var ids = new List<string>();
            var collections = PHAssetCollection.FetchAssetCollections([folderId], null);
            if (collections.Count > 0 && collections.ObjectAt(0) is PHAssetCollection collection)
            {
                var assets = PHAsset.FetchAssets(collection, null);
                for (nint i = 0; i < assets.Count; i++)
                {
                    if (assets.ObjectAt(i) is PHAsset asset)
                        ids.Add(asset.LocalIdentifier);
                }
            }
            return ids;
        });

    public Task<long> GetFileSizeAsync(string photoId) => Task.Run(() =>
    {
        try
        {
            var asset = FetchAsset(photoId);
            if (asset is null)
                return 0L;

            var resources = PHAssetResource.GetAssetResources(asset);
            var resource = resources.FirstOrDefault(r =>
                    r.ResourceType is PHAssetResourceType.Photo or PHAssetResourceType.FullSizePhoto)
                ?? resources.FirstOrDefault();

            if (resource?.ValueForKey(new NSString("fileSize")) is NSNumber size)
                return size.LongValue;
        }
        catch
        {
            // private key lookup can fail — size is cosmetic, so 0 is fine
        }
        return 0L;
    });

    public async Task<bool> MoveToSystemTrashAsync(IReadOnlyList<string> photoIds)
    {
        if (photoIds.Count == 0)
            return true;

        var assets = FetchAssets(photoIds);
        if (assets.Length == 0)
            return true;

        // One system confirmation dialog for the whole batch; assets land in "Recently Deleted".
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PHPhotoLibrary.SharedPhotoLibrary.PerformChanges(
            () => PHAssetChangeRequest.DeleteAssets(assets),
            (success, _) => tcs.TrySetResult(success));
        return await tcs.Task;
    }

    public Task<IReadOnlyList<AlbumInfo>> GetAlbumsAsync() => Task.Run<IReadOnlyList<AlbumInfo>>(() =>
    {
        var list = new List<AlbumInfo>();
        var collections = PHAssetCollection.FetchAssetCollections(
            PHAssetCollectionType.Album, PHAssetCollectionSubtype.AlbumRegular, null);

        for (nint i = 0; i < collections.Count; i++)
        {
            if (collections.ObjectAt(i) is not PHAssetCollection collection)
                continue;

            list.Add(new AlbumInfo
            {
                Id = collection.LocalIdentifier,
                Name = collection.LocalizedTitle ?? "Album",
                Count = (int)PHAsset.FetchAssets(collection, null).Count,
            });
        }

        return list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    });

    public async Task<string?> MoveToAlbumAsync(string photoId, AlbumInfo album)
    {
        var asset = FetchAsset(photoId);
        if (asset is null)
            return null;

        var collections = PHAssetCollection.FetchAssetCollections([album.Id], null);
        if (collections.Count == 0 || collections.ObjectAt(0) is not PHAssetCollection collection)
            return null;

        // iOS albums are references, not folders — the photo is added to the album
        // (it stays in the main library too; that's how Photos works). Same asset, same id.
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PHPhotoLibrary.SharedPhotoLibrary.PerformChanges(
            () =>
            {
                var request = PHAssetCollectionChangeRequest.ChangeRequest(collection);
                request?.AddAssets(new PHObject[] { asset });
            },
            (success, _) => tcs.TrySetResult(success));
        return await tcs.Task ? photoId : null;
    }

    /// <summary>iOS offers no per-asset deep link into the Photos app.</summary>
    public bool SupportsOpenExternally => false;

    public Task<bool> OpenExternallyAsync(string photoId) => Task.FromResult(false);

    /// <summary>iOS photos live in the library, not folders — the album list already covers everything.</summary>
    public bool SupportsFolderBrowsing => false;

    public Task<AlbumInfo?> PickExternalFolderAsync() => Task.FromResult<AlbumInfo?>(null);

    public async Task<AlbumInfo?> CreateAlbumAsync(string name)
    {
        string? newId = null;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PHPhotoLibrary.SharedPhotoLibrary.PerformChanges(
            () =>
            {
                var request = PHAssetCollectionChangeRequest.CreateAssetCollection(name);
                newId = request.PlaceholderForCreatedAssetCollection.LocalIdentifier;
            },
            (success, _) => tcs.TrySetResult(success));

        var ok = await tcs.Task;
        return ok && newId is not null
            ? new AlbumInfo { Id = newId, Name = name, Count = 0 }
            : null;
    }

    private static PHAsset? FetchAsset(string photoId)
    {
        var result = PHAsset.FetchAssetsUsingLocalIdentifiers([photoId], null);
        return result.Count > 0 ? result.ObjectAt(0) as PHAsset : null;
    }

    private static PHAsset[] FetchAssets(IReadOnlyList<string> photoIds)
    {
        var result = PHAsset.FetchAssetsUsingLocalIdentifiers([.. photoIds], null);
        var assets = new List<PHAsset>((int)result.Count);
        for (nint i = 0; i < result.Count; i++)
        {
            if (result.ObjectAt(i) is PHAsset asset)
                assets.Add(asset);
        }
        return [.. assets];
    }
}
