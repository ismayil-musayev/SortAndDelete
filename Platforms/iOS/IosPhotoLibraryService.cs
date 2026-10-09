using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AVFoundation;
using CoreGraphics;
using CoreMedia;
using Foundation;
using Photos;
using SortAndDelete.Models;
using UIKit;

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

    /// <summary>
    /// How long an iCloud download under way may go without a progress report. Photos reports all
    /// through one, so this long without means it has stalled.
    /// </summary>
    private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a fetch may show no sign of life outside a download: waiting its turn behind
    /// other downloads, or preparing what it downloaded (rendering an edit, say), it reports
    /// nothing at all.
    /// </summary>
    private static readonly TimeSpan QuietLimit = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The longest a thumbnail may take, however steadily it downloads: Photos makes it from a
    /// small rendition, or at worst from one original photo.
    /// </summary>
    private static readonly TimeSpan ThumbnailLimit = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The longest a whole original may take, a clip to sample or a file to hash, however
    /// steadily it downloads: about 2 GB at 15 Mbit/s.
    /// </summary>
    private static readonly TimeSpan OriginalLimit = TimeSpan.FromMinutes(20);

    /// <summary>How long a frame may take to decode once its clip has been fetched.</summary>
    private static readonly TimeSpan DecodeLimit = TimeSpan.FromSeconds(30);

    /// <summary>Each chunk of a file being hashed is copied through a pooled buffer this big.</summary>
    private const int HashBufferSize = 128 * 1024;

    private readonly ClipCache _clips = new();

    public Task<byte[]?> GetThumbnailPixelsAsync(string photoId, int size, CancellationToken ct = default) =>
        Task.Run<byte[]?>(() =>
        {
            var asset = FetchAsset(photoId);
            if (asset is null)
                return null;

            var deadline = new FetchDeadline(ThumbnailLimit);
            using var options = new PHImageRequestOptions
            {
                NetworkAccessAllowed = true, // pull from iCloud when the original is offloaded
                DeliveryMode = PHImageRequestOptionsDeliveryMode.HighQualityFormat,
                ResizeMode = PHImageRequestOptionsResizeMode.Exact,
                ProgressHandler = (double progress, NSError? error, out bool stop, NSDictionary? _) =>
                    stop = deadline.Report(progress, error),
            };

            // Asynchronous, so that a download that stalls is given up on instead of holding one
            // of the scan's slots for good. The handler may run on the main thread, so all it does
            // is hand the image over; it is drawn here.
            var delivery = new Handover<UIImage>();
            int request = PHImageManager.DefaultManager.RequestImageForAsset(
                asset,
                new CGSize(ThumbnailSourceSize, ThumbnailSourceSize),
                // Fit, not Fill: Apple documents an exact resize as cropping Fill to the square,
                // where Android squashes the whole photo.
                PHImageContentMode.AspectFit,
                options,
                (image, info) =>
                {
                    // A cancelled or failed request ends here, stand-in or not. Otherwise a
                    // low-quality stand-in, which HighQualityFormat shouldn't send anyway, is
                    // followed by the final image.
                    if (Failed(info))
                        delivery.Fail(image);
                    else if (IsDegraded(info))
                        delivery.Drop(image);
                    else
                        delivery.Deliver(image);
                });

            var thumbnail = delivery.Await(deadline.TimeLeft, () =>
            {
                deadline.GiveUp();
                PHImageManager.DefaultManager.CancelImageRequest(request);
            }, ct);

            // Both wrappers go now: a scan makes little managed garbage, so the GC might leave the
            // bitmap alive for a long time.
            try
            {
                using (thumbnail)
                using (var frame = thumbnail?.CGImage)
                    return frame is null ? null : ToPackedRgb(frame, size);
            }
            catch
            {
                return null; // unreadable — the item simply never matches
            }
        }, ct);

    public Task<byte[]?> GetVideoFramePixelsAsync(string photoId, double position, int size, CancellationToken ct = default) =>
        Task.Run<byte[]?>(() =>
        {
            var asset = FetchAsset(photoId);
            if (asset is null)
                return null;

            // The scan asks for a clip's positions one after another, and they share one fetch.
            using var lease = _clips.Get(photoId, VersionOf(asset), () => FetchClip(asset, ct));
            if (lease is null)
                return null; // not fetched, now or a moment ago — the clip just never matches

            try
            {
                return ReadFrame(lease.Clip, position, size, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return null; // leave the frame unsampled — the clip just never matches
            }
        }, ct);

    /// <summary>
    /// Fetches the clip, downloading it from iCloud when it has been offloaded: the whole
    /// original, which can take minutes. Waits for as long as the download keeps moving, up to
    /// <see cref="OriginalLimit"/>, and null when it fails or stalls.
    /// </summary>
    private static AVAsset? FetchClip(PHAsset asset, CancellationToken ct)
    {
        var deadline = new FetchDeadline(OriginalLimit);
        using var options = new PHVideoRequestOptions
        {
            NetworkAccessAllowed = true,

            // The full-quality clip, as before. A cheaper format would do for a 32-pixel grid, but
            // nothing documents it decoding to the same frames, and other frames would mean other
            // fingerprints.
            DeliveryMode = PHVideoRequestOptionsDeliveryMode.HighQualityFormat,

            ProgressHandler = (double progress, NSError? error, out bool stop, NSDictionary? _) =>
                stop = deadline.Report(progress, error),
        };

        // Photos calls back on a queue of its choosing, possibly after this has given up: the
        // handler only hands the clip over.
        var delivery = new Handover<AVAsset>();
        int request = PHImageManager.DefaultManager.RequestAVAsset(asset, options, (video, _, info) =>
        {
            if (Failed(info))
                delivery.Fail(video);
            else
                delivery.Deliver(video);
        });

        return delivery.Await(deadline.TimeLeft, () =>
        {
            deadline.GiveUp();
            PHImageManager.DefaultManager.CancelImageRequest(request);
        }, ct);
    }

    /// <summary>
    /// The frame <paramref name="position"/> of the way through <paramref name="clip"/>, packed
    /// like a thumbnail. Decoded here, not in a Photos handler, which would hold up its queue.
    /// </summary>
    private static byte[]? ReadFrame(AVAsset clip, double position, int size, CancellationToken ct)
    {
        var duration = clip.Duration;
        if (!duration.IsNumeric || duration.Seconds <= 0)
            return null;

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

        // AVFoundation calls back on a queue of its own, possibly after this has given up, and
        // the frame it hands over is ours to release.
        var delivery = new Handover<CGImage>();
        using var time = NSValue.FromCMTime(FrameTime(duration, position));
        generator.GenerateCGImagesAsynchronously(
            [time],
            (CMTime _, CGImage? image, CMTime _, AVAssetImageGeneratorResult result, NSError? _) =>
            {
                if (result == AVAssetImageGeneratorResult.Succeeded)
                    delivery.Deliver(image);
                else
                    delivery.Fail(image);
            });

        using var frame = delivery.Await(Within(DecodeLimit), generator.CancelAllCGImageGeneration, ct);
        return frame is null ? null : ToPackedRgb(frame, size);
    }

    /// <summary>
    /// The moment <paramref name="position"/> (0 to 1) of the way through. Kept a tick short of
    /// the end: the last frame starts before the duration does, so with no tolerance either side
    /// a time at the very end asks for a frame that isn't there.
    /// </summary>
    private static CMTime FrameTime(CMTime duration, double position)
    {
        var at = CMTime.FromSeconds(duration.Seconds * Math.Clamp(position, 0, 1), 600);
        return CMTime.GetMinimum(at, duration - new CMTime(1, 600));
    }

    public Task<string?> ComputeContentHashAsync(string photoId, CancellationToken ct = default) =>
        Task.Run<string?>(() =>
        {
            var asset = FetchAsset(photoId);
            if (asset is null)
                return null;

            // Edits never touch the original, so an edited copy would hash like its unedited twin.
            if (asset.HasAdjustments)
                return null;

            // The original only, never an edited rendition or another of the asset's resources:
            // the very file GetFileSizeAsync measures.
            var resource = OriginalResource(asset);
            if (resource is null)
                return null;

            // Photos calls back one call at a time on a serial queue of its own, possibly after
            // this has given up, and an exception thrown back into native code ends the app. So
            // only the callbacks touch the hasher and the buffer (the completion disposes the one
            // and returns the other to the pool), and the semaphore is never disposed and has no
            // maximum, so Release can't throw.
            var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[]? buffer = ArrayPool<byte>.Shared.Rent(HashBufferSize);
            var finished = new SemaphoreSlim(0);
            bool failed = false;
            string? hash = null;

            // Data coming in counts as movement as well as progress: a file read from the phone
            // reports no progress at all. Apple names no queue for the progress handler, so it
            // touches nothing but the deadline, which locks.
            var deadline = new FetchDeadline(OriginalLimit);
            using var options = new PHAssetResourceRequestOptions
            {
                NetworkAccessAllowed = true,
                ProgressHandler = progress => deadline.Report(progress, null),
            };

            int request = PHAssetResourceManager.DefaultManager.RequestData(
                resource,
                options,
                data =>
                {
                    try
                    {
                        deadline.DataArrived();

                        // Copied through the pooled buffer: a new array for every chunk would churn
                        // the large-object heap all through a clip of several gigabytes. Nothing
                        // comes after the completion, but should anything, the buffer is gone.
                        if (!failed && buffer is not null)
                        {
                            nint bytes = data.Bytes;
                            long length = (long)data.Length;
                            for (long offset = 0; offset < length; offset += buffer.Length)
                            {
                                int count = (int)Math.Min(buffer.Length, length - offset);
                                Marshal.Copy(bytes + (nint)offset, buffer, 0, count);
                                hasher.AppendData(buffer, 0, count);
                            }
                        }
                    }
                    catch
                    {
                        failed = true;
                    }
                    finally
                    {
                        // Each chunk's wrapper keeps the chunk alive until it is collected, and
                        // with next to no managed garbage left to prompt a collection, a large
                        // file's chunks would pile up. So it goes now.
                        Discard(data);
                    }
                },
                error =>
                {
                    try
                    {
                        if (buffer is not null)
                            ArrayPool<byte>.Shared.Return(buffer);
                        buffer = null;

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
                completed = WaitWithin(finished, deadline.TimeLeft, ct);
            }
            finally
            {
                // Stalled, too slow or cancelled: stop Photos reading, perhaps downloading, the rest.
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

            // The very file ComputeContentHashAsync reads, so identical files measure the same.
            if (OriginalResource(asset)?.ValueForKey(new NSString("fileSize")) is NSNumber size)
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

    /// <summary>
    /// The file an asset's size describes and its hash reads: the original video of a clip, the
    /// original photo of anything else, and never an edited rendition, a Live Photo's paired
    /// video or any other resource. Measuring one file and hashing another would give
    /// byte-identical files different sizes, and they would never be compared.
    /// </summary>
    private static PHAssetResource? OriginalResource(PHAsset asset)
    {
        var original = asset.MediaType == PHAssetMediaType.Video
            ? PHAssetResourceType.Video
            : PHAssetResourceType.Photo;
        return PHAssetResource.GetAssetResources(asset).FirstOrDefault(r => r.ResourceType == original);
    }

    /// <summary>Moves on with every change to the asset, edits included.</summary>
    private static double VersionOf(PHAsset asset) => asset.ModificationDate?.SecondsSinceReferenceDate ?? 0;

    /// <summary>
    /// Whether a Photos result handler's info says the request was cancelled or failed. Never
    /// throws, as it runs inside the handler; info it can't read counts as a failure.
    /// </summary>
    private static bool Failed(NSDictionary? info)
    {
        try
        {
            return info?[PHImageKeys.Cancelled] is NSNumber { BoolValue: true }
                || info?[PHImageKeys.Error] is NSError;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Whether an image handler got a low-quality stand-in for the final image. Never throws, as
    /// it runs inside the handler.
    /// </summary>
    private static bool IsDegraded(NSDictionary? info)
    {
        try
        {
            return info?[PHImageKeys.ResultIsDegraded] is NSNumber { BoolValue: true };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Disposes a wrapper a callback brought that won't be used, and stands null in its place.
    /// Never throws, as callbacks use it.
    /// </summary>
    private static T? Discard<T>(T? unwanted) where T : class, IDisposable
    {
        try
        {
            unwanted?.Dispose();
        }
        catch
        {
            // nothing may throw back into native code
        }
        return null;
    }

    /// <summary>A deadline <paramref name="limit"/> from now, as <see cref="WaitWithin"/> takes one.</summary>
    private static Func<TimeSpan> Within(TimeSpan limit)
    {
        long until = Environment.TickCount64 + (long)limit.TotalMilliseconds;
        return () => TimeSpan.FromMilliseconds(until - Environment.TickCount64);
    }

    /// <summary>
    /// Waits for <paramref name="signal"/> while <paramref name="timeLeft"/> says there is time,
    /// asking it again each time that runs out, so a deadline that moves on is followed. False
    /// once it runs out; a cancelled <paramref name="ct"/> throws OperationCanceledException.
    /// </summary>
    private static bool WaitWithin(SemaphoreSlim signal, Func<TimeSpan> timeLeft, CancellationToken ct)
    {
        TimeSpan left;
        while ((left = timeLeft()) > TimeSpan.Zero)
        {
            if (signal.Wait(left, ct))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Hands one result over from a Photos or AVFoundation callback to the thread waiting for it.
    /// </summary>
    /// <remarks>
    /// The frameworks call back on queues of their choosing, possibly after the wait has given up,
    /// and an exception thrown back into native code ends the app. So nothing the callbacks call
    /// here throws, the semaphore is never disposed and has no maximum, and a result that is
    /// unwanted or comes too late is disposed of there and then rather than left to the GC. Never
    /// the one handed over, though: the runtime keeps one wrapper per native object, so should
    /// Photos send that object again, it comes in the very wrapper the waiter is using.
    /// </remarks>
    private sealed class Handover<T> where T : class, IDisposable
    {
        private readonly object _gate = new();
        private readonly SemaphoreSlim _arrived = new(0);
        private T? _result;
        private T? _handedOver;
        private bool _settled;

        /// <summary>For the callback: the result, null for none. The first to settle it counts.</summary>
        public void Deliver(T? result) => Settle(result, keep: true);

        /// <summary>For the callback: the request failed, and nothing is handed over.</summary>
        public void Fail(T? unwanted) => Settle(unwanted, keep: false);

        /// <summary>For the callback: <paramref name="unwanted"/> is dropped, and the wait goes on.</summary>
        public void Drop(T? unwanted)
        {
            try
            {
                lock (_gate)
                {
                    if (ReferenceEquals(unwanted, _handedOver))
                        unwanted = null;
                }
            }
            catch
            {
                unwanted = null; // nothing may throw back into native code; the GC will see to it
            }

            Discard(unwanted);
        }

        private void Settle(T? result, bool keep)
        {
            try
            {
                lock (_gate)
                {
                    if (!_settled)
                    {
                        _settled = true;
                        if (keep)
                        {
                            _result = _handedOver = result;
                            result = null;
                        }
                    }

                    if (ReferenceEquals(result, _handedOver))
                        result = null;
                }
            }
            catch
            {
                result = null; // nothing may throw back into native code; the GC will see to it
            }

            Discard(result);
            _arrived.Release();
        }

        /// <summary>
        /// Waits for the result as <see cref="WaitWithin"/> does. Giving up calls
        /// <paramref name="cancel"/> to stop the request, and returns null. A cancelled
        /// <paramref name="ct"/> stops the request too, and throws OperationCanceledException.
        /// </summary>
        public T? Await(Func<TimeSpan> timeLeft, Action cancel, CancellationToken ct)
        {
            bool arrived = false;
            try
            {
                arrived = WaitWithin(_arrived, timeLeft, ct);
            }
            finally
            {
                if (!arrived)
                {
                    cancel();
                    Discard(Take()); // anything handed over since the last look is too late now
                }
            }

            return Take();
        }

        /// <summary>Takes the result, if one came; anything handed over afterwards is too late.</summary>
        private T? Take()
        {
            lock (_gate)
            {
                _settled = true;
                var result = _result;
                _result = null;
                return result;
            }
        }
    }

    /// <summary>
    /// How long to keep waiting on a fetch that may come down from iCloud, never past
    /// <c>limit</c>. While a download is under way, Photos reports progress all through it, so
    /// <see cref="StallLimit"/> without a report means it has stalled. Before it starts, though,
    /// the request may be waiting its turn behind other downloads, and once it ends Photos may
    /// still be preparing the result, and neither reports anything. Then it waits
    /// <see cref="QuietLimit"/> from the last sign of life, its own or any other fetch's: a
    /// queue that moves will reach it.
    /// </summary>
    private sealed class FetchDeadline(TimeSpan limit)
    {
        /// <summary>When any fetch last moved.</summary>
        private static long s_lastMovedAnywhere;

        private readonly object _gate = new();
        private readonly long _started = Environment.TickCount64;
        private long _lastMoved;
        private bool _underWay;
        private double _progress = double.NaN;
        private volatile bool _givenUp;

        /// <summary>
        /// For the request's progress handler: notes any movement, and answers whether to stop
        /// the download. Never throws.
        /// </summary>
        public bool Report(double progress, NSError? error)
        {
            try
            {
                lock (_gate)
                {
                    // Any change counts, not only a rise, but the same figure again doesn't, and
                    // neither does an error. Neither 0 nor 1 is under way: at 0 nothing has come
                    // down yet and the request may still be queued, and at 1 the download is over.
                    if (error is null && progress != _progress)
                    {
                        _progress = progress;
                        Moved(underWay: progress is > 0 and < 1);
                    }
                }
            }
            catch
            {
                // nothing may throw back into native code
            }
            return _givenUp;
        }

        /// <summary>For a data handler: data coming in is movement too. Never throws.</summary>
        public void DataArrived()
        {
            try
            {
                lock (_gate)
                    Moved(underWay: true);
            }
            catch
            {
                // nothing may throw back into native code
            }
        }

        private void Moved(bool underWay)
        {
            long now = Environment.TickCount64;
            _lastMoved = now;
            _underWay = underWay;
            Volatile.Write(ref s_lastMovedAnywhere, now);
        }

        /// <summary>How much longer to wait: nothing left means give up.</summary>
        public TimeSpan TimeLeft()
        {
            long until;
            lock (_gate)
            {
                until = _underWay
                    ? _lastMoved + (long)StallLimit.TotalMilliseconds
                    : Math.Max(Math.Max(_started, _lastMoved), Volatile.Read(ref s_lastMovedAnywhere))
                        + (long)QuietLimit.TotalMilliseconds;
            }

            long cappedAt = _started + (long)limit.TotalMilliseconds;
            return TimeSpan.FromMilliseconds(Math.Min(until, cappedAt) - Environment.TickCount64);
        }

        /// <summary>From now on, the progress handler tells Photos to stop downloading as well.</summary>
        public void GiveUp() => _givenUp = true;
    }

    /// <summary>
    /// Clips fetched from Photos, kept a short while so that a clip's sample positions, which the
    /// scan asks for one after another, share a single fetch: fetching an offloaded clip can mean
    /// downloading its whole original from iCloud. A failed fetch is remembered too, so the
    /// clip's remaining positions give up at once instead of each waiting all over again.
    /// </summary>
    private sealed class ClipCache
    {
        /// <summary>The scan samples four clips at a time; the other two are margin.</summary>
        private const int Capacity = 6;

        /// <summary>How long a clip is kept after it was last read from.</summary>
        private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

        /// <summary>
        /// How long a failed fetch is remembered: long enough for the clip's other positions, which
        /// follow it straight away, to give up at once. A scan run again within it skips the clip
        /// too.
        /// </summary>
        private static readonly TimeSpan FailureLifetime = TimeSpan.FromSeconds(15);

        private readonly object _gate = new();
        private readonly List<CachedClip> _entries = [];

        /// <summary>
        /// The holds on each clip: one for the entry caching it, one for each reader. The runtime
        /// keeps one wrapper per native object, so should Photos hand the same clip over again,
        /// entries share a wrapper, and it is disposed only once nothing holds it. That is decided
        /// and done under the lock, so nothing can take the wrapper up in between.
        /// </summary>
        private readonly Dictionary<AVAsset, int> _holds = new(ReferenceEqualityComparer.Instance);

        /// <summary>Empties the cache once it has gone a lifetime unused, not at the next scan.</summary>
        private readonly Timer _sweeper;

        public ClipCache() => _sweeper = new Timer(_ => Sweep());

        /// <summary>
        /// This version of the clip, fetched through <paramref name="fetch"/> unless it is cached,
        /// and lent until the lease is disposed. Null when the fetch fails, or failed a moment ago.
        /// </summary>
        public Lease? Get(string photoId, double version, Func<AVAsset?> fetch)
        {
            try
            {
                lock (_gate)
                {
                    DropExpired();
                    if (_entries.Find(e => e.PhotoId == photoId) is { } cached)
                    {
                        if (cached.Version == version)
                            return cached.Clip is null ? null : Lend(cached);

                        Remove(cached); // the clip has changed since
                    }
                }
            }
            finally
            {
                Reschedule();
            }

            var clip = fetch(); // outside the lock: it can take minutes

            try
            {
                lock (_gate)
                {
                    // Another call fetched the same clip meanwhile. If it got one, that serves this
                    // read too, and this fetch's clip goes unless something holds it already.
                    var raced = _entries.Find(e => e.PhotoId == photoId);
                    if (raced?.Clip is not null && raced.Version == version)
                    {
                        if (clip is not null && !_holds.ContainsKey(clip))
                            Discard(clip);
                        return Lend(raced);
                    }

                    // Held before the entry it replaces lets go, which may hold the same wrapper.
                    var entry = new CachedClip(photoId, version, clip);
                    if (clip is not null)
                        Hold(clip);
                    if (raced is not null)
                        Remove(raced);
                    _entries.Add(entry);

                    var lease = clip is null ? null : Lend(entry);
                    TrimToCapacity();
                    return lease;
                }
            }
            finally
            {
                Reschedule();
            }
        }

        private Lease Lend(CachedClip entry)
        {
            entry.Readers++;
            entry.LastUsed = Environment.TickCount64;
            Hold(entry.Clip!);
            return new Lease(this, entry);
        }

        private void Return(CachedClip entry)
        {
            lock (_gate)
            {
                entry.Readers--;
                entry.LastUsed = Environment.TickCount64;
                Release(entry.Clip!);
            }

            Reschedule();
        }

        /// <summary>Takes the entry out of the cache; its clip goes once nothing else holds it.</summary>
        private void Remove(CachedClip entry)
        {
            _entries.Remove(entry);
            if (entry.Clip is not null)
                Release(entry.Clip);
        }

        private void Hold(AVAsset clip) =>
            _holds[clip] = _holds.TryGetValue(clip, out int holds) ? holds + 1 : 1;

        /// <summary>Lets go of one hold on the clip, and disposes it if that was the last.</summary>
        private void Release(AVAsset clip)
        {
            if (_holds.TryGetValue(clip, out int holds) && holds > 1)
            {
                _holds[clip] = holds - 1;
                return;
            }

            _holds.Remove(clip);
            Discard(clip);
        }

        private void DropExpired()
        {
            long now = Environment.TickCount64;
            foreach (var entry in _entries.Where(e => IsDue(e, now)).ToList())
                Remove(entry);
        }

        /// <summary>Removes the clips longest unused, of those nobody is reading, down to the capacity.</summary>
        private void TrimToCapacity()
        {
            while (_entries.Count > Capacity
                && _entries.Where(e => e.Readers == 0).MinBy(e => e.LastUsed) is { } oldest)
            {
                Remove(oldest);
            }
        }

        private static bool IsDue(CachedClip entry, long now) =>
            entry.Readers == 0
            && now - entry.LastUsed >= (long)(entry.Clip is null ? FailureLifetime : Lifetime).TotalMilliseconds;

        /// <summary>The timer's: drops whatever has expired. Nothing may throw out of a timer callback.</summary>
        private void Sweep()
        {
            try
            {
                bool more;
                lock (_gate)
                {
                    DropExpired();
                    more = _entries.Count > 0;
                }

                if (more)
                    Reschedule();
            }
            catch
            {
                // the next call sweeps instead
            }
        }

        /// <summary>Puts the sweep off until the cache has gone a lifetime unused.</summary>
        private void Reschedule() =>
            _sweeper.Change(Lifetime + TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

        /// <summary>One clip, or the failure to fetch it.</summary>
        internal sealed class CachedClip(string photoId, double version, AVAsset? clip)
        {
            public string PhotoId { get; } = photoId;

            /// <summary>The asset's version when fetched, so an edit is never served from before it.</summary>
            public double Version { get; } = version;

            /// <summary>Null when the fetch failed.</summary>
            public AVAsset? Clip { get; } = clip;

            public long LastUsed { get; set; } = Environment.TickCount64;

            /// <summary>While anyone reads it, it neither expires nor makes room: more positions follow.</summary>
            public int Readers { get; set; }
        }

        /// <summary>A reader's hold on a cached clip, which keeps it from being disposed meanwhile.</summary>
        internal sealed class Lease(ClipCache cache, CachedClip entry) : IDisposable
        {
            private int _returned;

            public AVAsset Clip { get; } = entry.Clip!;

            /// <summary>Gives the clip back; only the first call counts.</summary>
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _returned, 1) == 0)
                    cache.Return(entry);
            }
        }
    }
}
