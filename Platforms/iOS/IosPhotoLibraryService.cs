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

    /// <summary>iOS manages a limited selection in its own UI; the app doesn't offer it here.</summary>
    public Task<bool> HasLimitedAccessAsync() => Task.FromResult(false);

    public Task<bool> SelectMorePhotosAsync() => RequestAccessAsync();

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

            // Kept in UTC, unlike takenAt: it only has to notice that the item changed, and a
            // change of time zone isn't one.
            var modifiedAt = asset.ModificationDate is NSDate modified
                ? (DateTime)modified
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
                ModifiedAt = modifiedAt,
                IsEdited = asset.HasAdjustments,
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
                // Fit, not Fill: Apple documents an exact resize as cropping Fill to the square,
                // where Android squashes the whole photo.
                PHImageContentMode.AspectFit,
                options,
                (image, _) =>
                {
                    // Nothing may throw back into native code. Both wrappers go now: a scan makes
                    // little managed garbage, so the GC might leave the bitmap alive for a long time.
                    try
                    {
                        using (image)
                        using (var frame = image?.CGImage)
                        {
                            if (frame is not null)
                                pixels = ToPackedRgb(frame, size);
                        }
                    }
                    catch
                    {
                        // unreadable — the item simply never matches
                    }
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

            AVAsset? delivered = null;

            // Photos calls back on a queue of its choosing, possibly after this has given up, and
            // an exception thrown back into native code ends the app: the handler only hands the
            // clip over, and the semaphore, never disposed and with no maximum, can't throw.
            var finished = new SemaphoreSlim(0);

            int request = PHImageManager.DefaultManager.RequestAVAsset(asset, options, (video, _, _) =>
            {
                delivered = video;
                finished.Release();
            });

            bool answered = false;
            try
            {
                answered = finished.Wait(TimeSpan.FromSeconds(30), ct);
            }
            finally
            {
                // Timed out or cancelled: stop Photos fetching the clip, perhaps from iCloud.
                if (!answered)
                    PHImageManager.DefaultManager.CancelImageRequest(request);
            }

            using var clip = answered ? delivered : null;
            if (clip is null)
                return null;

            // Decoded here, not in the handler: that would hold up Photos' queue, and spend the
            // 30 seconds the fetch gets.
            try
            {
                using var generator = new AVAssetImageGenerator(clip)
                {
                    AppliesPreferredTrackTransform = true,

                    // The box Android's GetScaledFrameAtTime fits its frame into.
                    MaximumSize = new CGSize(ThumbnailSourceSize, ThumbnailSourceSize),

                    // Exact, not nearest-keyframe: a re-encode lays its keyframes out
                    // differently, so snapping to one would sample a different moment in
                    // each copy and the frames would never line up.
                    RequestedTimeToleranceBefore = CMTime.Zero,
                    RequestedTimeToleranceAfter = CMTime.Zero,
                };

                var at = CMTime.FromSeconds(clip.Duration.Seconds * Math.Clamp(position, 0, 1), 600);
                using var frame = generator.CopyCGImageAtTime(at, out _, out var error);
                return frame is not null && error is null ? ToPackedRgb(frame, size) : null;
            }
            catch
            {
                return null; // leave the frame unsampled — the clip just never matches
            }
        }, ct);

    public Task<string?> ComputeContentHashAsync(string photoId, CancellationToken ct = default) =>
        Task.Run<string?>(() =>
        {
            var asset = FetchAsset(photoId);
            if (asset is null)
                return null;

            // Edits never touch the original, so an edited copy would hash like its unedited twin.
            if (asset.HasAdjustments)
                return null;

            // The original only, never an edited rendition or another of the asset's resources.
            var resource = PHAssetResource.GetAssetResources(asset).FirstOrDefault(r =>
                r.ResourceType is PHAssetResourceType.Photo or PHAssetResourceType.Video);
            if (resource is null)
                return null;

            // Photos calls back one call at a time on a serial queue of its own, possibly after
            // this has given up, and an exception thrown back into native code ends the app. So
            // only the callbacks touch the hasher (the completion disposes it), and the semaphore
            // is never disposed and has no maximum, so Release can't throw.
            var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var finished = new SemaphoreSlim(0);
            bool failed = false;
            string? hash = null;

            using var options = new PHAssetResourceRequestOptions { NetworkAccessAllowed = true };
            int request = PHAssetResourceManager.DefaultManager.RequestData(
                resource,
                options,
                data =>
                {
                    try
                    {
                        hasher.AppendData(data.ToArray());
                    }
                    catch
                    {
                        failed = true;
                    }
                },
                error =>
                {
                    try
                    {
                        if (!failed && error is null)
                            hash = Convert.ToHexString(hasher.GetHashAndReset());
                        hasher.Dispose();
                    }
                    catch
                    {
                        // no hash — the file just never matches
                    }
                    finished.Release();
                });

            bool completed = false;
            try
            {
                completed = finished.Wait(TimeSpan.FromMinutes(5), ct);
            }
            finally
            {
                // Timed out or cancelled: stop Photos reading, perhaps downloading, the rest.
                if (!completed)
                    PHAssetResourceManager.DefaultManager.CancelDataRequest(request);
            }
            return completed ? hash : null;
        }, ct);

    /// <summary>
    /// Squashes the image into a square and unpacks it to RGB triples.
    /// </summary>
    /// <remarks>
    /// Rows come out top first, as on Android. Core Graphics' origin is at the bottom left, but
    /// DrawImage draws the image upright, and a bitmap context stores its top row first.
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

    /// <summary>Any album takes any asset, with nothing to ask first.</summary>
    public async Task<IReadOnlyList<MoveTarget>> GetMoveTargetsAsync(MediaKind kind) =>
        (await GetAlbumsAsync()).Select(album => new MoveTarget(album, NeedsFolderAccess: false)).ToList();

    public Task<FolderAccessResult> RequestFolderAccessAsync(AlbumInfo album) =>
        Task.FromResult(FolderAccessResult.Granted);

    public async Task<MoveResult> MoveToAlbumAsync(string photoId, AlbumInfo album, MoveFeedback? feedback = null)
    {
        var asset = FetchAsset(photoId);
        if (asset is null)
            return MoveResult.Failed("This photo is no longer in your library.");

        var collections = PHAssetCollection.FetchAssetCollections([album.Id], null);
        if (collections.Count == 0 || collections.ObjectAt(0) is not PHAssetCollection collection)
            return MoveResult.Failed($"The album {album.Name} no longer exists.");

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
        return await tcs.Task
            ? MoveResult.Moved(photoId)
            : MoveResult.Failed($"Photos didn't add it to {album.Name}.");
    }

    /// <summary>iOS offers no per-asset deep link into the Photos app.</summary>
    public bool SupportsOpenExternally => false;

    public Task<bool> OpenExternallyAsync(string photoId) => Task.FromResult(false);

    /// <summary>iOS photos live in the library, not folders — the album list already covers everything.</summary>
    public bool SupportsFolderBrowsing => false;

    public Task<FolderPick> PickExternalFolderAsync(MediaKind kind) => Task.FromResult(FolderPick.None);

    public async Task<AlbumInfo?> CreateAlbumAsync(string name, MediaKind kind)
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
