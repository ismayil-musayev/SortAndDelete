using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using Microsoft.Win32.SafeHandles;
using SortAndDelete.Logic;
using SortAndDelete.Models;
using System.Security.Cryptography;
using AndroidUri = Android.Net.Uri;
using Application = Android.App.Application;

namespace SortAndDelete.Services;

/// <summary>MediaStore-backed photo/video library for Android 10+ (scoped storage).</summary>
public sealed class AndroidPhotoLibraryService : IPhotoLibraryService
{
    // MediaStore.Files.FileColumns values / names (string literals: the binding names vary by API level)
    private const int MediaTypeImage = 1;
    private const int MediaTypeVideo = 3;
    private const string ColMediaType = "media_type";
    private const string ColDuration = "duration";
    private const string ColIsFavorite = "is_favorite"; // API 30+
    private const string ColVolumeName = "volume_name";
    private const string ColIsPending = "is_pending";

    private static ContentResolver Resolver =>
        Application.Context.ContentResolver ?? throw new InvalidOperationException("No ContentResolver.");

    /// <summary>The files table covers both images and videos with the same _id space.</summary>
    private static AndroidUri FilesUri =>
        MediaStore.Files.GetContentUri("external") ?? throw new InvalidOperationException("No MediaStore URI.");

    /// <summary>
    /// createTrashRequest/createWriteRequest reject files-table URIs ("All requested items
    /// must be Media items") — they need typed images/video URIs. We remember each id's
    /// kind from queries and fall back to a trashed-inclusive lookup for unknown ids.
    /// </summary>
    private readonly Dictionary<string, MediaKind> _kindCache = [];

    /// <summary>Android 11+ has a real system trash with the 30-day auto purge.</summary>
    public bool SupportsSystemTrash => OperatingSystem.IsAndroidVersionAtLeast(30);

    /// <summary>Items we trashed keep their MediaStore row, so we can un-trash them (Android 11+).</summary>
    public bool CanRestoreFromSystemTrash => OperatingSystem.IsAndroidVersionAtLeast(30);

    public async Task<bool> RequestAccessAsync()
    {
        // Ask only when there is no access yet. With the Android 14+ "selected photos" subset,
        // READ_MEDIA_IMAGES stays denied, and asking again doesn't show a dialog: it reopens the
        // system photo picker. Home asks on every appearance, so the picker kept coming back.
        if (await Permissions.CheckStatusAsync<GalleryPermission>() == PermissionStatus.Granted ||
            await HasSelectedPhotosAccessAsync())
            return true;

        if (await Permissions.RequestAsync<GalleryPermission>() == PermissionStatus.Granted)
            return true;

        // Android 14+: the user may have granted access to selected photos only.
        return await HasSelectedPhotosAccessAsync();
    }

    public async Task<bool> HasLimitedAccessAsync() =>
        await Permissions.CheckStatusAsync<GalleryPermission>() != PermissionStatus.Granted &&
        await HasSelectedPhotosAccessAsync();

    public async Task<bool> SelectMorePhotosAsync()
    {
        // With selected photos only, asking again opens the system photo picker on the current
        // selection (or the user allows all) — exactly once, because the user asked for it.
        if (await Permissions.RequestAsync<GalleryPermission>() == PermissionStatus.Granted)
            return true;
        return await HasSelectedPhotosAccessAsync();
    }

    private static async Task<bool> HasSelectedPhotosAccessAsync() =>
        OperatingSystem.IsAndroidVersionAtLeast(34) &&
        await Permissions.CheckStatusAsync<PartialGalleryPermission>() == PermissionStatus.Granted;

    public Task<IReadOnlyList<PhotoAsset>> GetAllPhotosAsync() => Task.Run<IReadOnlyList<PhotoAsset>>(() =>
    {
        var list = new List<PhotoAsset>();

        bool hasFavoriteColumn = OperatingSystem.IsAndroidVersionAtLeast(30);
        List<string> projection =
        [
            MediaStore.Images.Media.InterfaceConsts.Id,
            MediaStore.Images.Media.InterfaceConsts.DateTaken,
            MediaStore.Images.Media.InterfaceConsts.DateModified,
            MediaStore.Images.Media.InterfaceConsts.DateAdded,
            MediaStore.Images.Media.InterfaceConsts.Size,
            MediaStore.Images.Media.InterfaceConsts.Width,
            MediaStore.Images.Media.InterfaceConsts.Height,
            MediaStore.Images.Media.InterfaceConsts.RelativePath,
            MediaStore.Images.Media.InterfaceConsts.DisplayName,
            ColMediaType,
            ColDuration,
        ];
        if (hasFavoriteColumn)
            projection.Add(ColIsFavorite);

        // Trashed items are excluded from queries by default, so the bin/committed
        // photos disappear from here automatically.
        using var cursor = Resolver.Query(
            FilesUri,
            [.. projection],
            $"{ColMediaType} IN ({MediaTypeImage}, {MediaTypeVideo})",
            null,
            $"{MediaStore.Images.Media.InterfaceConsts.DateTaken} DESC");

        if (cursor is null)
            return list;

        int idCol = cursor.GetColumnIndexOrThrow(MediaStore.Images.Media.InterfaceConsts.Id);
        int takenCol = cursor.GetColumnIndexOrThrow(MediaStore.Images.Media.InterfaceConsts.DateTaken);
        int modifiedCol = cursor.GetColumnIndexOrThrow(MediaStore.Images.Media.InterfaceConsts.DateModified);
        int addedCol = cursor.GetColumnIndexOrThrow(MediaStore.Images.Media.InterfaceConsts.DateAdded);
        int sizeCol = cursor.GetColumnIndexOrThrow(MediaStore.Images.Media.InterfaceConsts.Size);
        int widthCol = cursor.GetColumnIndexOrThrow(MediaStore.Images.Media.InterfaceConsts.Width);
        int heightCol = cursor.GetColumnIndexOrThrow(MediaStore.Images.Media.InterfaceConsts.Height);
        int pathCol = cursor.GetColumnIndexOrThrow(MediaStore.Images.Media.InterfaceConsts.RelativePath);
        int nameCol = cursor.GetColumnIndexOrThrow(MediaStore.Images.Media.InterfaceConsts.DisplayName);
        int typeCol = cursor.GetColumnIndexOrThrow(ColMediaType);
        int durationCol = cursor.GetColumnIndexOrThrow(ColDuration);
        int favoriteCol = hasFavoriteColumn ? cursor.GetColumnIndexOrThrow(ColIsFavorite) : -1;

        while (cursor.MoveToNext())
        {
            // DATE_TAKEN comes from EXIF; items without EXIF (screenshots, messenger
            // downloads) fall back to the file time. DATE_MODIFIED/DATE_ADDED are seconds.
            long takenMs = cursor.GetLong(takenCol);
            if (takenMs <= 0)
                takenMs = cursor.GetLong(modifiedCol) * 1000L;
            if (takenMs <= 0)
                takenMs = cursor.GetLong(addedCol) * 1000L;

            bool isVideo = cursor.GetInt(typeCol) == MediaTypeVideo;
            string relativePath = cursor.IsNull(pathCol) ? "" : cursor.GetString(pathCol) ?? "";
            string id = cursor.GetLong(idCol).ToString();

            lock (_kindCache)
                _kindCache[id] = isVideo ? MediaKind.Video : MediaKind.Image;

            list.Add(new PhotoAsset
            {
                Id = id,
                Kind = isVideo ? MediaKind.Video : MediaKind.Image,
                TakenAt = DateTimeOffset.FromUnixTimeMilliseconds(takenMs).ToLocalTime().DateTime,
                SizeBytes = cursor.GetLong(sizeCol),
                Width = cursor.GetInt(widthCol),
                Height = cursor.GetInt(heightCol),
                DurationMs = isVideo ? cursor.GetLong(durationCol) : 0,
                FileName = cursor.IsNull(nameCol) ? "" : cursor.GetString(nameCol) ?? "",
                FolderPath = relativePath,
                IsFavorite = favoriteCol >= 0 && cursor.GetInt(favoriteCol) == 1,
            });
        }

        return list;
    });

    public Task<Stream?> GetImageStreamAsync(string photoId, int maxPixelSize, CancellationToken ct = default) =>
        Task.Run<Stream?>(() =>
        {
            try
            {
                var uri = UriFor(photoId);

                bool isVideo;
                lock (_kindCache)
                    isVideo = _kindCache.GetValueOrDefault(photoId) == MediaKind.Video;

                // Big requests (the swipe card) decode the ORIGINAL file — LoadThumbnail
                // often serves a small cached thumbnail regardless of the requested size,
                // which looks soft blown up to screen size. Small requests (covers, grids)
                // and video posters stay on the fast thumbnail path.
                if (!isVideo && maxPixelSize > 512)
                {
                    var original = DecodeOriginal(uri, maxPixelSize);
                    if (original is not null)
                        return original;
                }

                using var bitmap = Resolver.LoadThumbnail(uri,
                    new Android.Util.Size(maxPixelSize, maxPixelSize), null);
                return CompressToStream(bitmap, 88);
            }
            catch
            {
                return null; // item gone or unreadable — the Image control just shows nothing
            }
        }, ct);

    /// <summary>Full-fidelity decode of the source file, downscaled to fit and EXIF-rotated.</summary>
    private Stream? DecodeOriginal(AndroidUri uri, int maxPixelSize)
    {
        try
        {
            var source = Android.Graphics.ImageDecoder.CreateSource(Resolver, uri);
            using var bitmap = Android.Graphics.ImageDecoder.DecodeBitmap(
                source, new SizeCapListener(maxPixelSize));
            return CompressToStream(bitmap, 92);
        }
        catch
        {
            return null; // unsupported format — the caller falls back to the thumbnail
        }
    }

    public Task<byte[]?> GetThumbnailPixelsAsync(string photoId, int size, CancellationToken ct = default) =>
        Task.Run<byte[]?>(() =>
        {
            try
            {
                // The MediaStore thumbnail on purpose: it is cached and fast, and across a
                // whole library that matters far more than fidelity does at 32 pixels square.
                using var thumbnail = Resolver.LoadThumbnail(UriFor(photoId),
                    new Android.Util.Size(ThumbnailSourceSize, ThumbnailSourceSize), null);
                return thumbnail is null ? null : ToPackedRgb(thumbnail, size);
            }
            catch
            {
                return null; // unreadable or unsupported — the item simply never matches
            }
        }, ct);

    public Task<byte[]?> GetVideoFramePixelsAsync(string photoId, double position, int size, CancellationToken ct = default) =>
        Task.Run<byte[]?>(() =>
        {
            Android.Media.MediaMetadataRetriever? retriever = null;
            try
            {
                retriever = new Android.Media.MediaMetadataRetriever();
                retriever.SetDataSource(Application.Context, UriFor(photoId));

                if (!long.TryParse(retriever.ExtractMetadata(Android.Media.MetadataKey.Duration), out long durationMs)
                    || durationMs <= 0)
                    return null;

                long timeUs = (long)(durationMs * Math.Clamp(position, 0, 1)) * 1000L;

                // Closest, not ClosestSync: a re-encode lays its keyframes out differently, so
                // snapping to one would sample a different moment in each copy and the frames
                // would never line up.
                using var frame = retriever.GetScaledFrameAtTime(
                    timeUs, Android.Media.Option.Closest, ThumbnailSourceSize, ThumbnailSourceSize);
                return frame is null ? null : ToPackedRgb(frame, size);
            }
            catch
            {
                return null;
            }
            finally
            {
                retriever?.Release();
                retriever?.Dispose();
            }
        }, ct);

    public Task<string?> ComputeContentHashAsync(string photoId, CancellationToken ct = default) =>
        Task.Run<string?>(() =>
        {
            try
            {
                using var stream = Resolver.OpenInputStream(UriFor(photoId));
                if (stream is null)
                    return null;

                using var hasher = System.Security.Cryptography.IncrementalHash.CreateHash(
                    System.Security.Cryptography.HashAlgorithmName.SHA256);

                var buffer = new byte[128 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    hasher.AppendData(buffer, 0, read);
                }

                return Convert.ToHexString(hasher.GetHashAndReset());
            }
            catch (System.OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }, ct);

    /// <summary>Bitmaps are fetched at this size and squared down from there.</summary>
    private const int ThumbnailSourceSize = 256;

    /// <summary>Squares the bitmap to <paramref name="size"/> and unpacks it to RGB triples.</summary>
    private static byte[] ToPackedRgb(Android.Graphics.Bitmap source, int size)
    {
        var scaled = Android.Graphics.Bitmap.CreateScaledBitmap(source, size, size, filter: true)
            ?? throw new InvalidOperationException("Bitmap could not be scaled.");
        try
        {
            var pixels = new int[size * size];
            scaled.GetPixels(pixels, 0, size, 0, 0, size, size);

            var rgb = new byte[size * size * 3];
            for (int i = 0; i < pixels.Length; i++)
            {
                int pixel = pixels[i]; // packed ARGB
                rgb[i * 3] = (byte)((pixel >> 16) & 0xFF);
                rgb[(i * 3) + 1] = (byte)((pixel >> 8) & 0xFF);
                rgb[(i * 3) + 2] = (byte)(pixel & 0xFF);
            }
            return rgb;
        }
        finally
        {
            // CreateScaledBitmap hands back the original when the size already matches;
            // disposing it then would take the caller's bitmap with it.
            if (!ReferenceEquals(scaled, source))
                scaled.Dispose();
        }
    }

    private static Stream CompressToStream(Android.Graphics.Bitmap bitmap, int quality)
    {
        var ms = new MemoryStream();
        bitmap.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, quality, ms);
        ms.Position = 0;
        return ms;
    }

    private sealed class SizeCapListener(int maxPixelSize)
        : Java.Lang.Object, Android.Graphics.ImageDecoder.IOnHeaderDecodedListener
    {
        public void OnHeaderDecoded(Android.Graphics.ImageDecoder decoder,
            Android.Graphics.ImageDecoder.ImageInfo info,
            Android.Graphics.ImageDecoder.Source source)
        {
            // Software bitmaps so Compress() can read the pixels.
            decoder.Allocator = Android.Graphics.ImageDecoderAllocator.Software;

            int width = info.Size.Width, height = info.Size.Height;
            int longEdge = Math.Max(width, height);
            if (longEdge > maxPixelSize)
            {
                float scale = (float)maxPixelSize / longEdge;
                decoder.SetTargetSize(
                    Math.Max(1, (int)(width * scale)),
                    Math.Max(1, (int)(height * scale)));
            }
        }
    }

    public Task<IReadOnlyCollection<string>> GetAlbumMemberIdsAsync(string folderId) =>
        Task.Run<IReadOnlyCollection<string>>(() =>
        {
            var ids = new List<string>();
            using var cursor = Resolver.Query(FilesUri,
                [MediaStore.Images.Media.InterfaceConsts.Id],
                $"{MediaStore.Images.Media.InterfaceConsts.RelativePath} = ? AND {ColMediaType} IN ({MediaTypeImage}, {MediaTypeVideo})",
                [folderId],
                null);
            if (cursor is not null)
            {
                while (cursor.MoveToNext())
                    ids.Add(cursor.GetLong(0).ToString());
            }
            return ids;
        });

    public Task<long> GetFileSizeAsync(string photoId) => Task.Run(() =>
    {
        try
        {
            using var cursor = Resolver.Query(UriFor(photoId),
                [MediaStore.Images.Media.InterfaceConsts.Size], null, null, null);
            if (cursor is not null && cursor.MoveToFirst())
                return cursor.GetLong(0);
        }
        catch
        {
            // fall through
        }
        return 0L;
    });

    public Task<bool> MoveToSystemTrashAsync(IReadOnlyList<string> photoIds) => SetTrashedAsync(photoIds, trashed: true);

    public Task<bool> RestoreFromSystemTrashAsync(IReadOnlyList<string> photoIds) => SetTrashedAsync(photoIds, trashed: false);

    private async Task<bool> SetTrashedAsync(IReadOnlyList<string> photoIds, bool trashed)
    {
        if (photoIds.Count == 0)
            return true;

        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var uris = await ResolveTypedUrisAsync(photoIds);
            try
            {
                // System trash: auto-purged by Android after ~30 days; the same API restores (value=false).
                var pending = MediaStore.CreateTrashRequest(Resolver, uris, trashed);
                return await IntentSenderLauncher.LaunchAsync(pending.IntentSender);
            }
            catch (Java.Lang.Exception)
            {
                return false; // e.g. item vanished or provider rejected the request
            }
        }

        if (!trashed)
            return false; // no system trash to restore from on Android 10

        // Android 10 fallback: no system trash — permanent delete, through the folders the user
        // opens for the app.
        return await DeleteViaSafAsync(photoIds);
    }

    /// <summary>
    /// An item to delete on Android 10: its file, the size MediaStore knows for it (-1 when it
    /// knows none), and that file's id at the storage provider.
    /// </summary>
    private sealed record FileToDelete(string PhotoId, string Path, string MimeType, long Size, string DocumentId);

    /// <summary>
    /// Android 10: deletes items for good through the system's storage provider, which removes
    /// the file and its MediaStore row. MediaStore itself can't: asked to delete another app's
    /// photo after the user's consent, it drops the row but keeps the file, and the next media
    /// scan brings the photo back. The provider deletes only inside folders the user opened for
    /// the app, so every item's folder is granted first (see <see cref="GrantFoldersAsync"/>),
    /// and nothing is deleted unless all are. True once every item is gone.
    /// </summary>
    private static async Task<bool> DeleteViaSafAsync(IReadOnlyList<string> photoIds)
    {
        var files = await Task.Run(() => FilesToDelete(photoIds));
        if (files is null)
            return false; // nothing was deleted

        var grants = await Task.Run(PersistedFolderGrants);
        if (!await GrantFoldersAsync(files, grants))
            return false; // nothing was deleted

        // Each file has a grant now; one that can't be deleted doesn't keep the others.
        bool allGone = true;
        foreach (var file in files)
        {
            var (tree, documentId) = GrantFor(grants, file)!.Value;
            allGone &= await DeleteDocumentAsync(file, tree, documentId);
        }
        return allGone;
    }

    /// <summary>
    /// The file of each item still in MediaStore (an item without a row is gone already), or null
    /// when an item can't be looked up, or its file is somewhere the storage provider doesn't
    /// delete from (not internal storage or an SD card). Blocking.
    /// </summary>
    private static List<FileToDelete>? FilesToDelete(IReadOnlyList<string> photoIds)
    {
        try
        {
            var files = new List<FileToDelete>();
            foreach (var photoId in photoIds)
            {
                using var cursor = Resolver.Query(UriFor(photoId),
                    ["_data", MediaStore.Images.Media.InterfaceConsts.MimeType, MediaStore.Images.Media.InterfaceConsts.Size],
                    null, null, null);
                if (cursor is null)
                    return null;
                if (!cursor.MoveToFirst())
                    continue; // deleted already

                var path = cursor.IsNull(0) ? null : cursor.GetString(0);
                if (path is null || StorageDocuments.DocumentIdOfPath(path, InternalStoragePath) is not { } documentId)
                    return null;

                var mimeType = cursor.IsNull(1) ? null : cursor.GetString(1);
                long size = cursor.IsNull(2) ? -1 : cursor.GetLong(2); // unknown: no file matches it, so none is deleted
                files.Add(new FileToDelete(photoId, path, mimeType ?? "application/octet-stream", size, documentId));
            }
            return files;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Makes sure a folder grant covers every file: one kept from before, or a folder the user
    /// picks now. The picker opens on the folder <see cref="StorageDocuments.FirstFolderToGrant"/>
    /// names for the files still without a grant (the one nearest the top, so one grant covers the
    /// most), until none is left. A picked folder counts when it covers at least one of those
    /// files, a broader one too (the top of the storage, which Android has the user confirm): it
    /// is kept, so later deletes from it don't ask again, and added to <paramref name="grants"/>.
    /// Any other folder isn't kept, and the picker opens on the folder again. False once the user
    /// backs out.
    /// </summary>
    private static async Task<bool> GrantFoldersAsync(List<FileToDelete> files, List<(AndroidUri Tree, string DocumentId)> grants)
    {
        var missing = FilesWithoutGrant(files, grants);
        while (missing.Count > 0)
        {
            AndroidUri? treeUri;
            try
            {
                treeUri = await PickFolderAsync(StorageDocuments.FirstFolderToGrant(missing.Select(file => file.DocumentId))!);
            }
            catch (Exception)
            {
                return false; // Android couldn't show its folder picker
            }
            if (treeUri is null)
                return false; // backed out

            // Some other folder, or another provider's: not persisted, so its one-off grant lapses
            // when the phone restarts. The same files are missing, so the picker opens on the same
            // folder again.
            if (StorageTreeDocumentId(treeUri) is not { } treeDocumentId ||
                !missing.Any(file => StorageDocuments.DocumentIdInGrant(treeDocumentId, file.DocumentId) is not null))
                continue;

            try
            {
                Resolver.TakePersistableUriPermission(treeUri,
                    ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
            }
            catch (Java.Lang.Exception)
            {
                // not kept, but the grant still works for this delete
            }
            grants.Add((treeUri, treeDocumentId));
            missing = FilesWithoutGrant(files, grants);
        }
        return true;
    }

    /// <summary>The files no grant covers yet.</summary>
    private static List<FileToDelete> FilesWithoutGrant(List<FileToDelete> files, List<(AndroidUri Tree, string DocumentId)> grants) =>
        files.Where(file => GrantFor(grants, file) is null).ToList();

    /// <summary>A grant covering the file, and the file's document id inside it (see <see cref="StorageDocuments.DocumentIdInGrant"/>).</summary>
    private static (AndroidUri Tree, string DocumentId)? GrantFor(
        List<(AndroidUri Tree, string DocumentId)> grants, FileToDelete file)
    {
        foreach (var (tree, treeDocumentId) in grants)
        {
            if (StorageDocuments.DocumentIdInGrant(treeDocumentId, file.DocumentId) is { } documentId)
                return (tree, documentId);
        }
        return null;
    }

    /// <summary>
    /// Deletes an item's file through a folder grant (<paramref name="documentId"/> is its id
    /// inside <paramref name="tree"/>), then checks that it is gone: the storage provider no
    /// longer finds it, and the gallery no longer has the item, after the media scanner has read
    /// the path again should MediaStore still list it. The provider deletes a folder with
    /// everything inside it, so only the item's file is deleted (see <see cref="FindDocument"/>);
    /// anything else found there stays, and the item counts as not gone.
    /// </summary>
    private static async Task<bool> DeleteDocumentAsync(FileToDelete file, AndroidUri tree, string documentId)
    {
        var document = DocumentsContract.BuildDocumentUriUsingTree(tree, documentId)!;
        var found = await Task.Run(() => FindDocument(tree, document, file.Size));
        if (found == DocumentFound.Other)
            return false; // not the item's file, or it can't be told: left alone

        if (found == DocumentFound.ItemsFile)
        {
            await Task.Run(() =>
            {
                try
                {
                    DocumentsContract.DeleteDocument(Resolver, document);
                }
                catch (Java.Lang.Exception)
                {
                    // refused, gone already, or no such root (an SD card taken out): the checks below tell
                }
            });
            if (await Task.Run(() => FindDocument(tree, document, file.Size)) != DocumentFound.Nothing)
                return false;
        }

        if (!await Task.Run(() => IsInGallery(file.PhotoId)))
            return true;

        // The file is gone, but MediaStore still lists it: a scan of the missing file drops the row.
        await ScanAsync(file.Path, file.MimeType);
        return !await Task.Run(() => IsInGallery(file.PhotoId));
    }

    /// <summary>What the storage provider finds where an item's file is.</summary>
    private enum DocumentFound
    {
        /// <summary>The item's file: a file, not a folder, of the size MediaStore knows for the item.</summary>
        ItemsFile,

        /// <summary>Nothing: the file is gone (also when something else deleted it first).</summary>
        Nothing,

        /// <summary>A folder or a file of another size, or what is there can't be told.</summary>
        Other,
    }

    /// <summary>
    /// What the storage provider finds for an item's document inside a granted folder, asked
    /// through the grant for its type and size (<paramref name="size"/> is the one MediaStore
    /// knows for the item). A query on a missing document fails rather than coming back empty:
    /// the provider first checks that it lies inside the folder, which takes finding both. A
    /// folder that is gone, or an SD card taken out, fails the same way, so a failure counts as
    /// nothing found only while the folder itself is still found. Blocking.
    /// </summary>
    private static DocumentFound FindDocument(AndroidUri tree, AndroidUri document, long size)
    {
        try
        {
            using var cursor = Resolver.Query(document,
                [DocumentsContract.Document.ColumnMimeType, DocumentsContract.Document.ColumnSize], null, null, null);
            if (cursor is null || !cursor.MoveToFirst())
                return DocumentFound.Nothing;

            bool isFile = !cursor.IsNull(0) && cursor.GetString(0) != DocumentsContract.Document.MimeTypeDir;
            return isFile && !cursor.IsNull(1) && cursor.GetLong(1) == size ? DocumentFound.ItemsFile : DocumentFound.Other;
        }
        catch (Java.Lang.IllegalArgumentException)
        {
            return HasFolder(tree) ? DocumentFound.Nothing : DocumentFound.Other;
        }
        catch (Java.Lang.Exception)
        {
            return DocumentFound.Other; // e.g. no access: it can't be told
        }
    }

    /// <summary>Whether the storage provider still finds a granted folder itself. Blocking.</summary>
    private static bool HasFolder(AndroidUri tree)
    {
        try
        {
            var folder = DocumentsContract.BuildDocumentUriUsingTree(tree, DocumentsContract.GetTreeDocumentId(tree))!;
            using var cursor = Resolver.Query(folder, [DocumentsContract.Document.ColumnDocumentId], null, null, null);
            return cursor is not null && cursor.MoveToFirst();
        }
        catch (Java.Lang.Exception)
        {
            return false;
        }
    }

    public Task<IReadOnlyList<AlbumInfo>> GetAlbumsAsync() => Task.Run<IReadOnlyList<AlbumInfo>>(() =>
    {
        // Aggregate folders by relative path in C# — MediaStore has no portable GROUP BY,
        // and bucket display names collide between e.g. Pictures/Travel and DCIM/Travel.
        var byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        string[] projection = [MediaStore.Images.Media.InterfaceConsts.RelativePath];

        using (var cursor = Resolver.Query(FilesUri, projection,
                   $"{ColMediaType} IN ({MediaTypeImage}, {MediaTypeVideo})", null, null))
        {
            if (cursor is not null)
            {
                while (cursor.MoveToNext())
                {
                    var path = cursor.IsNull(0) ? null : cursor.GetString(0);
                    if (string.IsNullOrWhiteSpace(path))
                        continue;
                    byPath[path] = byPath.GetValueOrDefault(path) + 1;
                }
            }
        }

        // MediaStore only knows folders through the media inside them — brand-new/empty
        // folders are invisible to it, so also walk the real directory tree.
        foreach (var root in new[]
                 {
                     global::Android.OS.Environment.DirectoryPictures,
                     global::Android.OS.Environment.DirectoryDcim,
                     global::Android.OS.Environment.DirectoryMovies,
                 })
        {
            if (root is null)
                continue;
            try
            {
                var rootDir = global::Android.OS.Environment.GetExternalStoragePublicDirectory(root)?.AbsolutePath;
                if (rootDir is not null && Directory.Exists(rootDir))
                    CollectSubdirectories(rootDir, $"{root}/", depth: 2, byPath);
            }
            catch
            {
                // directory browsing can be restricted on some devices — MediaStore data still works
            }
        }

        var albums = byPath
            .Select(kv => new AlbumInfo
            {
                Id = kv.Key,
                Name = kv.Key.TrimEnd('/').Split('/').Last(),
                Count = kv.Value,
                Subtitle = kv.Key,
            })
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Folders the user picked with the system browser (anywhere on the device).
        albums.AddRange(GetSavedSafAlbums());
        return albums;
    });

    private static void CollectSubdirectories(string dir, string relativePrefix, int depth, Dictionary<string, int> byPath)
    {
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (string.IsNullOrEmpty(name) || name.StartsWith('.'))
                continue;

            var relative = $"{relativePrefix}{name}/";
            if (!byPath.ContainsKey(relative))
                byPath[relative] = 0;

            if (depth > 1)
            {
                try
                {
                    CollectSubdirectories(sub, relative, depth - 1, byPath);
                }
                catch
                {
                    // unreadable subtree — skip
                }
            }
        }
    }

    /// <summary>MediaStore takes videos under Pictures/ from Android 11 on.</summary>
    private static bool VideosAllowedInPictures => OperatingSystem.IsAndroidVersionAtLeast(30);

    /// <summary>
    /// Whether Android removes an original another app saved once the user agrees: from Android 11
    /// on (system trash, delete request). Android 10's MediaStore drops the original's row but
    /// keeps its file, and the next media scan brings it back. Android 10 deletes another app's
    /// file only through a folder the user opens for the app (see <see cref="DeleteViaSafAsync"/>),
    /// which a move that copies would have to ask for midway, so there every move is in place, or
    /// not offered.
    /// </summary>
    private static bool CanRemoveOriginals => OperatingSystem.IsAndroidVersionAtLeast(30);

    public async Task<IReadOnlyList<MoveTarget>> GetMoveTargetsAsync(MediaKind kind)
    {
        var albums = await GetAlbumsAsync();
        var grants = await Task.Run(PersistedFolderGrants);
        var galleryFolders = albums.Where(a => !IsBrowsedFolder(a)).Select(a => a.Id).ToList();

        var targets = new List<MoveTarget>();
        foreach (var album in albums)
        {
            if (IsBrowsedFolder(album))
            {
                if (IsOfferedBrowsedFolder(album, grants, galleryFolders) &&
                    (CanRemoveOriginals || MoveRouting.RouteFor(MoveRouting.RelativePathOfGrant(TreeDocumentIdOf(album)!),
                        kind, VideosAllowedInPictures) == MoveRoute.InPlace))
                    targets.Add(new MoveTarget(album, NeedsFolderAccess: false));
                continue;
            }

            var route = MoveRouting.RouteFor(album.Id, kind, VideosAllowedInPictures);
            if (!CanRemoveOriginals && route != MoveRoute.InPlace)
                continue; // Android 10: it would be a copy, and the original would stay

            switch (route)
            {
                case MoveRoute.NotPossible:
                    continue; // no app may write there, so it is never offered
                case MoveRoute.FolderAccess:
                    targets.Add(new MoveTarget(album, NeedsFolderAccess: FindFolderGrant(grants, album.Id) is null));
                    break;
                default:
                    targets.Add(new MoveTarget(album, NeedsFolderAccess: false));
                    break;
            }
        }
        return targets;
    }

    /// <summary>
    /// Whether a folder picked with the system folder browser is still offered: one this version
    /// would accept now (see <see cref="PickExternalFolderAsync"/>; older versions kept others),
    /// still granted (the grant can be withdrawn, and a restored backup never carries it), and
    /// not already listed. A browsed internal-storage folder that is also a gallery folder is
    /// listed once, as the gallery folder; gallery folders don't say which volume they are on,
    /// so a browsed SD card folder always stays.
    /// </summary>
    private static bool IsOfferedBrowsedFolder(AlbumInfo album,
        List<(AndroidUri Tree, string DocumentId)> grants, List<string> galleryFolders)
    {
        if (TreeDocumentIdOf(album) is not { } browsed || BrowsedFolderProblem(browsed) is not null)
            return false;

        if (!grants.Any(grant => grant.DocumentId == browsed))
            return false;

        return !browsed.StartsWith(PrimaryVolume, StringComparison.Ordinal) ||
               !galleryFolders.Any(folder => MoveRouting.IsGrantFor(browsed, folder));
    }

    /// <summary>
    /// Why Sort &amp; Delete won't move into a browsed internal-storage or SD card folder, or null
    /// when it will. The same folders are never offered from the gallery either.
    /// </summary>
    private static string? BrowsedFolderProblem(string treeDocumentId)
    {
        var relativePath = MoveRouting.RelativePathOfGrant(treeDocumentId);
        if (MoveRouting.IsAppFolder(relativePath))
            return AppFolderRefused;
        return relativePath.Length == 0 ? StorageTopRefused : null;
    }

    public async Task<FolderAccessResult> RequestFolderAccessAsync(AlbumInfo album)
    {
        // Open the picker on the album's own folder, so granting it takes two taps. Albums are
        // merged across volumes, so internal storage is the best guess; the user can browse on.
        if (await PickFolderAsync(PrimaryVolume + album.Id.Trim('/')) is not AndroidUri treeUri)
            return FolderAccessResult.Cancelled;

        // Some other folder: not persisted, so its one-off grant lapses when the phone restarts.
        var treeDocumentId = StorageTreeDocumentId(treeUri);
        if (treeDocumentId is null || !MoveRouting.GrantCovers(treeDocumentId, album.Id))
            return FolderAccessResult.OtherFolder;

        try
        {
            Resolver.TakePersistableUriPermission(treeUri,
                ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        }
        catch (Java.Lang.Exception)
        {
            // Moves look for persisted grants only, so the move will say access is missing.
        }
        return FolderAccessResult.Granted;
    }

    /// <summary>
    /// Opens the system folder picker on a folder of the device's storage (its document id,
    /// "primary:DCIM"), asking for lasting read and write access to the folder the user picks.
    /// The picked folder's tree uri, or null when the user backs out.
    /// </summary>
    private static async Task<AndroidUri?> PickFolderAsync(string folderDocumentId)
    {
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission
                        | ActivityFlags.GrantWriteUriPermission
                        | ActivityFlags.GrantPersistableUriPermission);
        intent.PutExtra(DocumentsContract.ExtraInitialUri,
            DocumentsContract.BuildDocumentUri(ExternalStorageAuthority, folderDocumentId));

        var result = await ActivityLauncher.LaunchForResultAsync(intent);
        return result?.Data;
    }

    /// <summary>The tree document id of a picked folder on the device's own storage; null for another provider's.</summary>
    private static string? StorageTreeDocumentId(AndroidUri treeUri) =>
        treeUri.Authority == ExternalStorageAuthority && DocumentsContract.IsTreeUri(treeUri)
            ? DocumentsContract.GetTreeDocumentId(treeUri)
            : null;

    public async Task<MoveResult> MoveToAlbumAsync(string photoId, AlbumInfo album, MoveFeedback? feedback = null)
    {
        var source = await Task.Run(() => IsInGallery(photoId) ? QuerySourceInfo(photoId) : null);
        if (source is null)
            return MoveResult.Failed("This photo is no longer in your gallery.");

        var job = new MoveJob(photoId, source, await KindOfAsync(photoId), album, feedback);

        if (IsBrowsedFolder(album))
            return await MoveIntoBrowsedFolderAsync(job);

        // "pictures/x/" is the same folder as "Pictures/x/", but Android 10 and 11 only move into
        // the standard spelling.
        var relativePath = MoveRouting.Canonical(string.IsNullOrEmpty(album.Id) ? $"Pictures/{album.Name}/" : album.Id);

        // Already in that folder: nothing to move, and no dialog to show for it.
        if (MoveRouting.IsSameFolder(source.RelativePath, relativePath))
            return MoveResult.Moved(photoId);

        // MediaStore moves a file in place only into the top-level folders its collection
        // allows; anywhere else the update throws, and so would a copy into that collection.
        switch (MoveRouting.RouteFor(relativePath, job.Kind, VideosAllowedInPictures))
        {
            case MoveRoute.NotPossible:
                return MoveResult.Failed(MoveRouting.IsAppFolder(relativePath)
                    ? AppFolderRefused
                    : $"Android doesn't let apps put files in {(album.Name.Length > 0 ? album.Name : "that folder")}.");

            case MoveRoute.Copy:
                // Download/ and Documents/ take new files of any type through the files table.
                return await MoveViaCopyAsync(job, relativePath, FilesUri);

            case MoveRoute.FolderAccess:
                var grant = FindFolderGrant(await Task.Run(PersistedFolderGrants), relativePath);
                return grant is { } granted
                    ? await MoveViaSafAsync(job, granted.Tree, granted.FolderDocumentId)
                    : MoveResult.Failed($"Sort & Delete has no access to {album.Name}. " +
                                        "Choose it again and allow access to the folder.");
        }

        // Files inside another app's Android/media directory (WhatsApp, Telegram, …)
        // can't be relocated by MediaStore at all — copy + delete instead.
        Func<Task<MoveResult>> copyInstead = () => MoveViaCopyAsync(job, relativePath, TypedCollectionFor(job.Kind));
        return MoveRouting.IsAppFolder(source.RelativePath)
            ? await copyInstead()
            : await MoveInPlaceAsync(job, relativePath, copyInstead);
    }

    /// <summary>
    /// Moves into a folder picked with the system folder browser, by the gallery's rules
    /// (<see cref="MoveRouting.RouteForBrowsed"/>): in place where MediaStore allows it, never into
    /// the top of the storage or an app's folder.
    /// </summary>
    private async Task<MoveResult> MoveIntoBrowsedFolderAsync(MoveJob job)
    {
        // Older versions also kept folders of other providers; those copies can't be checked
        // (see PickExternalFolderAsync), and the original is removed after the copy.
        var treeUri = AndroidUri.Parse(job.Album.Id[SafPrefix.Length..]);
        if (treeUri is null || TreeDocumentIdOf(job.Album) is not { } browsed)
            return MoveResult.Failed(OtherProviderRefused);

        var relativePath = MoveRouting.Canonical(MoveRouting.RelativePathOfGrant(browsed));
        switch (MoveRouting.RouteForBrowsed(browsed, job.Source.VolumeName, job.Source.RelativePath,
                    job.Kind, VideosAllowedInPictures))
        {
            case BrowsedRoute.NotPossible:
                return MoveResult.Failed(BrowsedFolderProblem(browsed) ?? CouldNotWriteInto(job.Album));

            case BrowsedRoute.AlreadyThere:
                return MoveResult.Moved(job.PhotoId); // a copy would only give it a new identity

            case BrowsedRoute.InPlace:
                // Should MediaStore still refuse, copy within the same volume.
                return await MoveInPlaceAsync(job, relativePath,
                    browsed.StartsWith(PrimaryVolume, StringComparison.Ordinal)
                        ? () => MoveViaCopyAsync(job, relativePath, TypedCollectionFor(job.Kind))
                        : () => MoveViaSafAsync(job, treeUri, folderDocumentId: null));

            case BrowsedRoute.MediaCopy:
                return await MoveViaCopyAsync(job, relativePath, TypedCollectionFor(job.Kind));

            case BrowsedRoute.FilesCopy:
                return await MoveViaCopyAsync(job, relativePath, FilesUri);

            default:
                return await MoveViaSafAsync(job, treeUri, folderDocumentId: null);
        }
    }

    /// <summary>
    /// Moves the item itself, after the write consent Android asks for: same id, every detail
    /// kept. Consent and move both go through the item's uri on its own volume
    /// (<see cref="TypedUriOnItsVolume"/>), so an item on an SD card moves within its card.
    /// <paramref name="copyInstead"/> runs when MediaStore still refuses the move.
    /// </summary>
    private async Task<MoveResult> MoveInPlaceAsync(MoveJob job, string relativePath, Func<Task<MoveResult>> copyInstead)
    {
        var uri = TypedUriOnItsVolume(job);
        bool moved;
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                // Ask for write access to this photo (system dialog), then move it.
                var pending = MediaStore.CreateWriteRequest(Resolver, new List<AndroidUri> { uri });
                if (!await IntentSenderLauncher.LaunchAsync(pending.IntentSender))
                    return MoveResult.Cancelled;
            }

            moved = await Task.Run(() => UpdateRelativePath(uri, relativePath));
        }
        catch (Java.Lang.SecurityException ex)
        {
            // Android 10 path: consent is requested via RecoverableSecurityException.
            if (ex is not RecoverableSecurityException recoverable)
                return MoveResult.Failed(RefusedMove(job.Album));

            if (!await IntentSenderLauncher.LaunchAsync(recoverable.UserAction.ActionIntent.IntentSender))
                return MoveResult.Cancelled;

            try
            {
                return await Task.Run(() => UpdateRelativePath(uri, relativePath))
                    ? MoveResult.Moved(job.PhotoId)
                    : MoveResult.Failed(RefusedMove(job.Album));
            }
            catch
            {
                return MoveResult.Failed(RefusedMove(job.Album));
            }
        }
        catch (Java.Lang.Exception)
        {
            moved = false; // some sources still refuse the in-place move
        }
        catch
        {
            return MoveResult.Failed(RefusedMove(job.Album));
        }

        // Moved in place: same id, all metadata intact. Otherwise copy, then remove the original.
        return moved ? MoveResult.Moved(job.PhotoId) : await copyInstead();
    }

    /// <summary>
    /// The item's typed uri on its own volume. MediaStore works out where a moved item goes on the
    /// volume its uri names, and up to Android 15 "external" names internal storage: through it,
    /// an item on an SD card would have to leave its card, which Android refuses ("Changing volume
    /// … not allowed"; Android 16 reads "external" as the item's own volume). So an SD card item
    /// goes through its card's volume ("1a2b-3c4d"). The write consent covers only the exact uri
    /// it was asked for, so it is asked for this one too.
    /// </summary>
    private static AndroidUri TypedUriOnItsVolume(MoveJob job)
    {
        if (IsOnInternalStorage(job.Source))
            return TypedUriFor(job.PhotoId, job.Kind);

        var collection = job.Kind == MediaKind.Video
            ? MediaStore.Video.Media.GetContentUri(job.Source.VolumeName)
            : MediaStore.Images.Media.GetContentUri(job.Source.VolumeName);
        return ContentUris.WithAppendedId(collection!, long.Parse(job.PhotoId));
    }

    private static bool IsOnInternalStorage(SourceInfo source) =>
        source.VolumeName is "" or "external" or "external_primary";

    private static string RefusedMove(AlbumInfo album) => $"Android refused to move it into {album.Name}.";

    private static string CouldNotCopyInto(AlbumInfo album, Exception? error = null) =>
        IsOutOfSpace(error)
            ? NoSpaceForCopy
            : $"Couldn't copy it into {album.Name}, so nothing was moved.";

    private static string CouldNotWriteInto(AlbumInfo album, Exception? error = null) =>
        IsOutOfSpace(error)
            ? NoSpaceForCopy
            : $"Couldn't write into {album.Name}, so nothing was moved. The folder may have been " +
              "deleted, or access to it withdrawn.";

    private const string NoSpaceForCopy = "There isn't enough free space for the copy, so nothing was moved.";

    private const string AppFolderRefused =
        "That folder belongs to another app, and Android deletes it with the app, so Sort & Delete " +
        "doesn't move photos there. Nothing was moved.";

    private const string StorageTopRefused =
        "Sort & Delete doesn't put photos at the top level of the storage. Choose a folder inside it, " +
        "or create one there.";

    private const string OtherProviderRefused =
        "Sort & Delete moves photos only into folders on this phone's own storage or its SD card, " +
        "where it can check the copy before it removes the original. Choose a folder there.";

    private static string LocationWouldBeLost(MoveJob job) =>
        "Moving it there means copying it, and Android won't let Sort & Delete read where it " +
        "was taken, so the copy would lose its location. Nothing was moved." + InPlaceHint(job);

    /// <summary>
    /// " DCIM and Pictures take it without a copy." (the folders for its kind, on its own volume:
    /// an item on an SD card moves within its card), or "" where every move is a copy: out of an
    /// app's folder, or into a browsed folder on another volume.
    /// </summary>
    private static string InPlaceHint(MoveJob job) =>
        MoveRouting.IsAppFolder(job.Source.RelativePath) || IsAcrossVolumes(job)
            ? ""
            : " " + MoveRouting.InWords(MoveRouting.InPlaceFoldersFor(job.Kind, VideosAllowedInPictures)) +
              " take it without a copy.";

    /// <summary>Whether the move goes into a browsed folder on another volume than the item's: a copy, whatever the folder.</summary>
    private static bool IsAcrossVolumes(MoveJob job) =>
        IsBrowsedFolder(job.Album) &&
        (TreeDocumentIdOf(job.Album) is not { } browsed || !MoveRouting.IsSameVolume(browsed, job.Source.VolumeName));

    private const string OriginalNotDeleted = "Android didn't remove the original, so nothing was moved.";

    private const string AndroidTenMovesInPlaceOnly =
        "On Android 10, Sort & Delete moves photos only within DCIM and Pictures, and videos within DCIM and " +
        "Movies. Anywhere else it would have to copy them, and removing the original would then need access " +
        "to its folder in the middle of the move. Choose a folder there.";

    /// <summary>
    /// Android 10, where Sort &amp; Delete moves only in place: why a move that needs a copy isn't
    /// made (see <see cref="CanRemoveOriginals"/>).
    /// </summary>
    private static string OriginalWouldStay(MoveJob job) =>
        MoveRouting.IsAppFolder(job.Source.RelativePath)
            ? "Moving it out of another app's folder means copying it, which Sort & Delete doesn't do on " +
              "Android 10: removing the original would need access to that folder in the middle of the move. " +
              "Nothing was moved."
            : "Moving it there means copying it, which Sort & Delete doesn't do on Android 10: removing the " +
              "original would need access to its folder in the middle of the move. Nothing was moved." +
              InPlaceHint(job);

    private static string OriginalLeftBehind(MoveJob job) =>
        $"It's in {job.Album.Name} now, but Android also kept the original in " +
        $"{FolderNameOf(job.Source.RelativePath)}, so it's in both places. The original stays in the " +
        "deck, so you can delete it if you don't need it.";

    private static string CopyKeptBesideOriginal(MoveJob job) =>
        $"The original stays in {FolderNameOf(job.Source.RelativePath)}, but Sort & Delete couldn't check that " +
        $"it is whole, so it kept the copy in {job.Album.Name} too. It's in both places. The original stays in " +
        "the deck, so you can delete it if you don't need it.";

    private static string FolderNameOf(string relativePath) =>
        relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "its old folder";

    private static bool IsOutOfSpace(Exception? error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e.Message.Contains("ENOSPC", StringComparison.Ordinal) ||
                e.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static AndroidUri TypedCollectionFor(MediaKind kind) =>
        kind == MediaKind.Video
            ? MediaStore.Video.Media.ExternalContentUri!
            : MediaStore.Images.Media.ExternalContentUri!;

    private async Task<MediaKind> KindOfAsync(string photoId)
    {
        await ResolveTypedUrisAsync([photoId]); // fills the kind cache
        lock (_kindCache)
            return _kindCache.GetValueOrDefault(photoId, MediaKind.Image);
    }

    private sealed record SourceInfo(
        string DisplayName,
        string MimeType,
        string RelativePath,
        long TakenMs,
        long ModifiedSec,
        string VolumeName,
        long SizeBytes,
        bool IsFavorite);

    /// <summary>One move: the item, what and where it is, where it goes, and the screen that asked.</summary>
    private sealed record MoveJob(string PhotoId, SourceInfo Source, MediaKind Kind, AlbumInfo Album, MoveFeedback? Feedback);

    private SourceInfo? QuerySourceInfo(string photoId)
    {
        bool hasFavorite = OperatingSystem.IsAndroidVersionAtLeast(30);
        List<string> projection =
        [
            MediaStore.Images.Media.InterfaceConsts.DisplayName,
            MediaStore.Images.Media.InterfaceConsts.MimeType,
            MediaStore.Images.Media.InterfaceConsts.RelativePath,
            MediaStore.Images.Media.InterfaceConsts.DateTaken,
            MediaStore.Images.Media.InterfaceConsts.DateModified,
            ColVolumeName,
            MediaStore.Images.Media.InterfaceConsts.Size,
        ];
        if (hasFavorite)
            projection.Add(ColIsFavorite);

        using var cursor = Resolver.Query(UriFor(photoId), [.. projection], null, null, null);
        if (cursor is null || !cursor.MoveToFirst())
            return null;

        return new SourceInfo(
            cursor.IsNull(0) ? "" : cursor.GetString(0) ?? "",
            cursor.IsNull(1) ? "application/octet-stream" : cursor.GetString(1) ?? "application/octet-stream",
            cursor.IsNull(2) ? "" : cursor.GetString(2) ?? "",
            cursor.IsNull(3) ? 0 : cursor.GetLong(3),
            cursor.IsNull(4) ? 0 : cursor.GetLong(4),
            cursor.IsNull(5) ? "" : cursor.GetString(5) ?? "",
            cursor.IsNull(6) ? 0 : cursor.GetLong(6),
            hasFavorite && !cursor.IsNull(7) && cursor.GetInt(7) == 1);
    }

    /// <summary>The bytes a copy is made from, or why there is no copy.</summary>
    private sealed record CopySource(AndroidUri? Uri, MoveResult? Refusal);

    /// <summary>
    /// The uri to copy an item's bytes from, location included. Android blanks the location in
    /// media the app didn't create unless the app holds ACCESS_MEDIA_LOCATION and asks for the
    /// original. Without that access (Android 14+ never grants it while the app may see selected
    /// photos only) the copy keeps every byte only if the item has no location to blank; when it
    /// has one, or Android can't say (videos), the user decides whether the copy may lose it.
    /// The original is removed after the copy, so nothing is lost without asking.
    /// </summary>
    private async Task<CopySource> OriginalForCopyAsync(MoveJob job)
    {
        bool granted = await Permissions.CheckStatusAsync<MediaLocationPermission>() == PermissionStatus.Granted ||
                       await MainThread.InvokeOnMainThreadAsync(() => Permissions.RequestAsync<MediaLocationPermission>())
                           == PermissionStatus.Granted;
        if (granted)
            return new CopySource(MediaStore.SetRequireOriginal(UriFor(job.PhotoId)), null);

        var readable = UriFor(job.PhotoId);
        if (job.Kind == MediaKind.Image && await Task.Run(() => HasNoLocation(readable, job.Source.MimeType)))
            return new CopySource(readable, null); // nothing blanked: every byte is the original's

        if (job.Feedback?.ConfirmAsync is not { } confirm)
            return new CopySource(null, MoveResult.Failed(LocationWouldBeLost(job)));

        var limited = await HasLimitedAccessAsync()
            ? "With access to selected photos only, Android hides where photos were taken. "
            : "Android won't let Sort & Delete read where it was taken. ";
        bool proceed = await MainThread.InvokeOnMainThreadAsync(() => confirm(
            "Its location would be lost",
            limited + $"Moving it to {job.Album.Name} means copying it, so the copy wouldn't keep its " +
            $"location.{InPlaceHint(job)} Move it anyway?",
            "Move anyway"));
        return proceed
            ? new CopySource(readable, null)
            : new CopySource(null, MoveResult.Cancelled);
    }

    /// <summary>
    /// Whether a photo certainly carries no location, read as the app sees it: Android blanks a
    /// location's values but keeps its tags, so a photo with a hidden location still shows them.
    /// Unknown counts as having one: a type ExifInterface can't read (it reports no tags rather
    /// than failing), a JPEG it didn't parse, and any XMP, which can hold a location too.
    /// Blocking.
    /// </summary>
    private static bool HasNoLocation(AndroidUri photo, string mimeType)
    {
        if (!ExifReadableTypes.Contains(mimeType.ToLowerInvariant()))
            return false;
        try
        {
            using var input = Resolver.OpenInputStream(photo);
            if (input is null)
                return false;
            var exif = new Android.Media.ExifInterface(input);
            if (mimeType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) &&
                exif.GetAttributeInt("ImageWidth", 0) == 0 && exif.GetAttributeInt("ImageLength", 0) == 0)
                return false; // a JPEG gives its size even without EXIF (missing reads as 0): this one wasn't read
            if (exif.GetAttribute("Xmp") is not null)
                return false;
            return GpsTags.All(tag => exif.GetAttribute(tag) is null);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The types ExifInterface reads.</summary>
    private static readonly string[] ExifReadableTypes =
        ["image/jpeg", "image/png", "image/webp", "image/heic", "image/heif", "image/x-adobe-dng"];

    /// <summary>Every EXIF GPS tag: Android blanks them all.</summary>
    private static readonly string[] GpsTags =
    [
        "GPSVersionID", "GPSLatitudeRef", "GPSLatitude", "GPSLongitudeRef", "GPSLongitude",
        "GPSAltitudeRef", "GPSAltitude", "GPSTimeStamp", "GPSSatellites", "GPSStatus", "GPSMeasureMode",
        "GPSDOP", "GPSSpeedRef", "GPSSpeed", "GPSTrackRef", "GPSTrack", "GPSImgDirectionRef",
        "GPSImgDirection", "GPSMapDatum", "GPSDestLatitudeRef", "GPSDestLatitude", "GPSDestLongitudeRef",
        "GPSDestLongitude", "GPSDestBearingRef", "GPSDestBearing", "GPSDestDistanceRef", "GPSDestDistance",
        "GPSProcessingMethod", "GPSAreaInformation", "GPSDateStamp", "GPSDifferential", "GPSHPositioningError",
    ];

    /// <summary>A copy, checked: how many bytes it holds, all of the original's.</summary>
    private sealed record CopyReport(long Length);

    /// <summary>Items at least this big show how far their copy has got.</summary>
    private const long ProgressFromBytes = 16L * 1024 * 1024;

    /// <summary>
    /// Copies every byte of <paramref name="original"/> (see <see cref="OriginalForCopyAsync"/>)
    /// into <paramref name="target"/>, has the copy written out to storage, then reads it back
    /// through a new descriptor: it must hold the original's length and the same SHA-256 as what
    /// was read, or this throws. What was read must also be as long as the original MediaStore
    /// knows (<paramref name="originalSize"/>; 0 when unknown): a stream Android converted (a
    /// transcoded video) or cut short would pass the other checks. Blanking a location keeps the
    /// length. The original is removed next, so nothing less may pass for a copy. Blocking: call
    /// it off the UI thread.
    /// </summary>
    private static CopyReport CopyVerified(AndroidUri original, AndroidUri target, long originalSize, IProgress<double>? progress)
    {
        var buffer = new byte[256 * 1024];
        long length = 0;
        byte[] copiedHash;
        CopyProgress? reporter;

        var source = Resolver.OpenFileDescriptor(original, "r") ?? throw new IOException("Could not open the original.");
        try
        {
            long expected = source.StatSize; // -1 when the size can't be known
            reporter = progress is not null && expected >= ProgressFromBytes ? new CopyProgress(progress, expected) : null;

            var destination = Resolver.OpenFileDescriptor(target, "w") ?? throw new IOException("Could not open the copy.");
            try
            {
                using var input = StreamOn(source, FileAccess.Read);
                using var output = StreamOn(destination, FileAccess.Write);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    hash.AppendData(buffer, 0, read);
                    length += read;
                    reporter?.Report(length);
                }
                output.Flush(flushToDisk: true); // fsync: on storage, not just handed to it
                copiedHash = hash.GetHashAndReset();
            }
            finally
            {
                CloseQuietly(destination);
            }

            if (expected >= 0 && length != expected)
                throw new IOException($"Copied {length} of {expected} bytes.");
            if (originalSize > 0 && length != originalSize)
                throw new IOException($"Read {length} bytes of an original of {originalSize}.");
        }
        finally
        {
            CloseQuietly(source);
        }

        // What storage holds now, read through a new descriptor.
        var check = Resolver.OpenFileDescriptor(target, "r") ?? throw new IOException("Could not reopen the copy.");
        try
        {
            if (check.StatSize >= 0 && check.StatSize != length)
                throw new IOException($"The copy holds {check.StatSize} of {length} bytes.");

            using var input = StreamOn(check, FileAccess.Read);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long readBack = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                readBack += read;
                reporter?.Report(length + readBack);
            }

            if (readBack != length || !hash.GetHashAndReset().AsSpan().SequenceEqual(copiedHash))
                throw new IOException("The copy doesn't match the original.");
        }
        finally
        {
            CloseQuietly(check);
        }

        reporter?.Report(2 * length);
        return new CopyReport(length);
    }

    /// <summary>A stream straight on the descriptor's file, which stays the caller's to close.</summary>
    private static FileStream StreamOn(ParcelFileDescriptor descriptor, FileAccess access) =>
        new(new SafeFileHandle(new IntPtr(descriptor.Fd), ownsHandle: false), access, bufferSize: 0);

    /// <summary>Closes the file (Dispose alone only drops the Java reference) and the reference.</summary>
    private static void CloseQuietly(ParcelFileDescriptor descriptor)
    {
        try
        {
            descriptor.Close();
        }
        catch (Java.IO.IOException)
        {
            // closing a read or a synced write loses nothing
        }
        descriptor.Dispose();
    }

    /// <summary>Reports a copy in whole percents: writing is the first half, reading it back the second.</summary>
    private sealed class CopyProgress(IProgress<double> progress, long length)
    {
        private int _percent = -1;

        public void Report(long done)
        {
            int percent = (int)Math.Min(100, done * 100 / (2 * length));
            if (percent == _percent)
                return;
            _percent = percent;
            progress.Report(percent / 100.0);
        }
    }

    /// <summary>The date the gallery shows for an item, in ms: when it was taken, else its file time.</summary>
    private static long DateOf(SourceInfo source) => source.TakenMs > 0 ? source.TakenMs : source.ModifiedSec * 1000L;

    /// <summary>
    /// Gives a copy the original's date (<see cref="DateOf"/>) and checks that it stuck. A copy is a
    /// new file, so Android dates it by when it was written unless it carries a capture date
    /// Android can read, and even then not always: an EXIF time without a time zone is read
    /// against the file's time. Needs write access to <paramref name="copy"/>: the app's own
    /// copy, or the grant <see cref="MediaUriOf"/> carries over. Blocking.
    /// </summary>
    private static bool KeepDate(AndroidUri copy, SourceInfo source)
    {
        long wanted = DateOf(source);
        if (wanted <= 0 || IsDated(copy, wanted))
            return true;

        try
        {
            var values = new ContentValues();
            values.Put(MediaStore.Images.Media.InterfaceConsts.DateTaken, wanted);
            Resolver.Update(copy, values, null, null);
        }
        catch (Java.Lang.Exception)
        {
            // refused: the check below says so
        }
        return IsDated(copy, wanted);
    }

    /// <summary>Whether MediaStore dates the item at <paramref name="wanted"/> (ms), the way the gallery reads it.</summary>
    private static bool IsDated(AndroidUri item, long wanted)
    {
        try
        {
            using var cursor = Resolver.Query(item,
                [MediaStore.Images.Media.InterfaceConsts.DateTaken, MediaStore.Images.Media.InterfaceConsts.DateModified],
                null, null, null);
            if (cursor is null || !cursor.MoveToFirst())
                return false;

            long taken = cursor.IsNull(0) ? 0 : cursor.GetLong(0);
            long date = taken > 0 ? taken : (cursor.IsNull(1) ? 0 : cursor.GetLong(1) * 1000L);
            return Math.Abs(date - wanted) < 1000;
        }
        catch (Java.Lang.Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Asks whether to move although the copy can't keep the original's date, before the original
    /// is touched. Without anyone to ask, the move goes ahead.
    /// </summary>
    private static async Task<bool> ConfirmRedatingAsync(MoveJob job)
    {
        if (job.Feedback?.ConfirmAsync is not { } confirm)
            return true;

        return await MainThread.InvokeOnMainThreadAsync(() => confirm(
            "It would be dated today",
            $"Android won't let Sort & Delete give the copy in {job.Album.Name} its original date, " +
            "so it would show up as taken today. Move it anyway?",
            "Move anyway"));
    }

    /// <summary>
    /// Move by copying through MediaStore (a new item the app owns, no consent needed), then
    /// removing the original (one consent dialog). The copy is written while pending, so no other
    /// app sees it, and MediaStore purges it should the move never finish; it is published only
    /// once checked (<see cref="CopyVerified"/>). Bytes are copied verbatim, so in-file metadata
    /// (EXIF date, GPS location, camera) survives; the date, file time and favourite are carried
    /// over explicitly. <paramref name="targetCollection"/> must accept the folder: the item's typed
    /// collection under DCIM/Pictures/Movies, the files table under Download/ and Documents/.
    /// Moved results carry the NEW item's id.
    /// </summary>
    private async Task<MoveResult> MoveViaCopyAsync(MoveJob job, string targetRelativePath, AndroidUri targetCollection)
    {
        if (!CanRemoveOriginals)
            return MoveResult.Failed(OriginalWouldStay(job));

        var source = job.Source;
        if (string.IsNullOrEmpty(source.DisplayName))
            return MoveResult.Failed(CouldNotCopyInto(job.Album));

        var copySource = await OriginalForCopyAsync(job);
        if (copySource.Uri is not { } original)
            return copySource.Refusal ?? MoveResult.Failed(LocationWouldBeLost(job));

        AndroidUri? copy = null;
        CopyReport copied;
        try
        {
            copied = await Task.Run(() =>
            {
                var values = new ContentValues();
                values.Put(MediaStore.Images.Media.InterfaceConsts.DisplayName, source.DisplayName);
                values.Put(MediaStore.Images.Media.InterfaceConsts.MimeType, source.MimeType);
                values.Put(MediaStore.Images.Media.InterfaceConsts.RelativePath, targetRelativePath);
                if (DateOf(source) > 0)
                    values.Put(MediaStore.Images.Media.InterfaceConsts.DateTaken, DateOf(source));
                values.Put(ColIsPending, 1);

                copy = Resolver.Insert(targetCollection, values) ?? throw new IOException("MediaStore refused the copy.");
                var report = CopyVerified(original, copy, source.SizeBytes, job.Feedback?.Progress);

                // The file time first, so publishing reads it: gallery apps date items without a
                // capture date by it.
                SetFileTime(copy, source.ModifiedSec);

                var publish = new ContentValues();
                publish.Put(ColIsPending, 0);
                if (source.IsFavorite && OperatingSystem.IsAndroidVersionAtLeast(30))
                    publish.Put(ColIsFavorite, 1); // keep the ⭐ flag on the copy

                // A copy left pending is purged by MediaStore within days, after the original is gone.
                if (Resolver.Update(copy, publish, null, null) <= 0)
                    throw new IOException("The copy could not be published.");
                return report;
            });
        }
        catch (Exception ex)
        {
            await Task.Run(() => TryDeleteOwnCopy(copy));
            return MoveResult.Failed(CouldNotCopyInto(job.Album, ex));
        }

        var published = copy!;
        var newId = ContentUris.ParseId(published).ToString();
        lock (_kindCache)
            _kindCache[newId] = job.Kind;

        if (!await Task.Run(() => KeepDate(published, source)) && !await ConfirmRedatingAsync(job))
        {
            await Task.Run(() => TryDeleteOwnCopy(published));
            return MoveResult.Cancelled;
        }

        return await FinishMoveAsync(job, copied, newId, () => TryDeleteOwnCopy(published));
    }

    /// <summary>
    /// The copy is in place and checked: removes the original behind one consent dialog (into the
    /// system trash on Android 11+, where it stays restorable for 30 days) and settles the
    /// result. <paramref name="rollBack"/> deletes the copy; it runs only when the original is
    /// certainly still there, whole: a consent dialog can end without reporting success although
    /// the original went, and report success although it stayed.
    /// </summary>
    private async Task<MoveResult> FinishMoveAsync(MoveJob job, CopyReport copied, string? newId, Action rollBack)
    {
        var removed = await RemoveOriginalAsync(job.PhotoId);
        bool stillThere = await Task.Run(() => IsInGallery(job.PhotoId));

        if (removed != MoveOutcome.Moved && stillThere && await Task.Run(() => OpensAtSize(job.PhotoId, copied.Length)))
        {
            await Task.Run(rollBack);
            return removed == MoveOutcome.Cancelled ? MoveResult.Cancelled : MoveResult.Failed(OriginalNotDeleted);
        }

        // The copy stays. If the original stayed too, it is in both places, and the user is told.
        if (!stillThere)
            return MoveResult.Moved(newId);
        return MoveResult.CopiedWithOriginalKept(newId,
            removed == MoveOutcome.Moved ? OriginalLeftBehind(job) : CopyKeptBesideOriginal(job));
    }

    /// <summary>
    /// Removes the original after its copy is checked, behind one consent dialog: into the system
    /// trash, so it can still be restored for 30 days. Moves copy only from Android 11 on (see
    /// <see cref="CanRemoveOriginals"/>), so on Android 10 this removes nothing and fails. Moved
    /// once Android reports it done, Cancelled when the user declines, Failed when Android
    /// refuses. Never throws, so the caller always gets to settle the copy.
    /// </summary>
    private async Task<MoveOutcome> RemoveOriginalAsync(string photoId)
    {
        // Not reached on Android 10, whose MediaStore would drop the original's row and keep its file.
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
            return MoveOutcome.Failed;

        try
        {
            var typedUris = await ResolveTypedUrisAsync([photoId]);
            var pending = MediaStore.CreateTrashRequest(Resolver, typedUris, true);
            return await IntentSenderLauncher.LaunchAsync(pending.IntentSender)
                ? MoveOutcome.Moved
                : MoveOutcome.Cancelled;
        }
        catch
        {
            return MoveOutcome.Failed;
        }
    }

    /// <summary>
    /// Whether the gallery still has the item: not deleted, and not in the system trash. Asks the
    /// collection: a query on the item's own uri finds it in the trash too. Blocking.
    /// </summary>
    private static bool IsInGallery(string photoId)
    {
        try
        {
            using var cursor = Resolver.Query(FilesUri, [MediaStore.Images.Media.InterfaceConsts.Id],
                $"{MediaStore.Images.Media.InterfaceConsts.Id} = ?", [photoId], null);
            return cursor is not null && cursor.MoveToFirst();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the item still opens at the length it was copied with. Unknown counts as no: a
    /// duplicate is better than losing the photo. Blocking.
    /// </summary>
    private static bool OpensAtSize(string photoId, long length)
    {
        try
        {
            var descriptor = Resolver.OpenFileDescriptor(UriFor(photoId), "r");
            if (descriptor is null)
                return false;
            try
            {
                return descriptor.StatSize == length;
            }
            finally
            {
                CloseQuietly(descriptor);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sets the file time of a copy the app owns to the original's (best effort; Android 10 gives
    /// apps no file access to shared storage). Blocking.
    /// </summary>
    private static void SetFileTime(AndroidUri ownUri, long modifiedSec)
    {
        if (modifiedSec <= 0)
            return;
        try
        {
            using var cursor = Resolver.Query(ownUri, ["_data"], null, null, null);
            if (cursor is not null && cursor.MoveToFirst() && !cursor.IsNull(0) && cursor.GetString(0) is { } path)
                TrySetFileTime(path, modifiedSec);
        }
        catch (Java.Lang.Exception)
        {
            // cosmetic: the date the app shows is kept by KeepDate
        }
    }

    private static bool TrySetFileTime(string path, long modifiedSec)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeSeconds(modifiedSec).UtcDateTime);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TryDeleteOwnCopy(AndroidUri? uri)
    {
        if (uri is null)
            return;
        try
        {
            Resolver.Delete(uri, null, null); // we created it, so no consent needed
        }
        catch
        {
            // best effort
        }
    }

    private static bool UpdateRelativePath(AndroidUri uri, string relativePath)
    {
        var values = new ContentValues();
        values.Put(MediaStore.Images.Media.InterfaceConsts.RelativePath, relativePath);
        return Resolver.Update(uri, values, null, null) > 0;
    }

    public Task<AlbumInfo?> CreateAlbumAsync(string name, MediaKind kind)
    {
        // On Android an album is just a folder — it comes into existence with the first
        // photo moved into it, so nothing to create up front. It goes where that move is in
        // place: Pictures/, or Movies/ for a video on Android 10.
        var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (string.IsNullOrWhiteSpace(safe))
            return Task.FromResult<AlbumInfo?>(null);

        var path = $"{MoveRouting.NewAlbumRoot(kind, VideosAllowedInPictures)}/{safe}/";
        return Task.FromResult<AlbumInfo?>(new AlbumInfo
        {
            Id = path,
            Name = safe,
            Count = 0,
            Subtitle = path,
        });
    }

    // ---------- Open in an external app ----------

    public bool SupportsOpenExternally => true;

    public async Task<bool> OpenExternallyAsync(string photoId)
    {
        var source = await Task.Run(() => QuerySourceInfo(photoId));
        var uri = (await ResolveTypedUrisAsync([photoId])).First();

        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(uri, source?.MimeType ?? "image/*");
        intent.AddFlags(ActivityFlags.GrantReadUriPermission);

        return await MainThread.InvokeOnMainThreadAsync(() =>
        {
            try
            {
                var activity = Platform.CurrentActivity
                    ?? throw new InvalidOperationException("No current activity.");
                activity.StartActivity(intent);
                return true;
            }
            catch (ActivityNotFoundException)
            {
                return false; // nothing on the device handles this type
            }
        });
    }

    // ---------- Browsed (SAF) folders: navigate anywhere in internal storage ----------

    private const string SafPrefix = "saf:";
    private const string SafFoldersPreferenceKey = "saf_folders"; // uri|uri|...

    public bool SupportsFolderBrowsing => true;

    public async Task<FolderPick> PickExternalFolderAsync(MediaKind kind)
    {
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission
                        | ActivityFlags.GrantWriteUriPermission
                        | ActivityFlags.GrantPersistableUriPermission);

        var result = await ActivityLauncher.LaunchForResultAsync(intent);
        if (result?.Data is not AndroidUri treeUri)
            return FolderPick.None;

        // Only folders on the device's own storage. Other providers (cloud, network, the
        // Downloads app) may store the copy only after reporting it written, or not at all, and
        // the original is removed next. Refused folders aren't kept, so their one-off grant lapses
        // when the phone restarts.
        var album = SafAlbumFor(treeUri.ToString()!);
        if (TreeDocumentIdOf(album) is not { } picked)
            return new FolderPick(Album: null, OtherProviderRefused);

        // The top of the storage (Android 10's picker offers it) or another app's folder.
        if (BrowsedFolderProblem(picked) is { } problem)
            return new FolderPick(Album: null, problem);

        if (!CanRemoveOriginals &&
            MoveRouting.RouteFor(MoveRouting.RelativePathOfGrant(picked), kind, VideosAllowedInPictures) != MoveRoute.InPlace)
            return new FolderPick(Album: null, AndroidTenMovesInPlaceOnly);

        try
        {
            // Keep write access across app restarts.
            Resolver.TakePersistableUriPermission(treeUri,
                ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        }
        catch
        {
            // best effort — the grant still works for this session
        }

        RememberSafFolder(treeUri.ToString()!);
        return new FolderPick(album);
    }

    private static void RememberSafFolder(string treeUri)
    {
        var saved = Preferences.Default.Get(SafFoldersPreferenceKey, "")
            .Split('|', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (!saved.Contains(treeUri))
        {
            saved.Add(treeUri);
            Preferences.Default.Set(SafFoldersPreferenceKey, string.Join('|', saved));
        }
    }

    private static IEnumerable<AlbumInfo> GetSavedSafAlbums() =>
        Preferences.Default.Get(SafFoldersPreferenceKey, "")
            .Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(SafAlbumFor);

    private static AlbumInfo SafAlbumFor(string treeUri)
    {
        // tree document ids look like "primary:MyAlbum/Sub" or "1B2C-3D4E:Foo" (SD card)
        string pretty;
        try
        {
            var docId = DocumentsContract.GetTreeDocumentId(AndroidUri.Parse(treeUri)!) ?? "";
            var parts = docId.Split(':', 2);
            var volume = parts[0] == "primary" ? "Internal storage" : $"SD card ({parts[0]})";
            pretty = parts.Length > 1 && parts[1].Length > 0 ? $"{volume}/{parts[1]}" : volume;
        }
        catch
        {
            pretty = "Browsed folder";
        }

        return new AlbumInfo
        {
            Id = SafPrefix + treeUri,
            Name = pretty.TrimEnd('/').Split('/').Last(),
            Count = -1, // unknown — not worth enumerating a whole tree for a label
            Subtitle = pretty,
        };
    }

    private static bool IsBrowsedFolder(AlbumInfo album) => album.Id.StartsWith(SafPrefix, StringComparison.Ordinal);

    /// <summary>The tree document id of a browsed internal-storage or SD card folder; null for other providers.</summary>
    private static string? TreeDocumentIdOf(AlbumInfo browsedFolder)
    {
        try
        {
            var tree = AndroidUri.Parse(browsedFolder.Id[SafPrefix.Length..]);
            return tree?.Authority == ExternalStorageAuthority ? DocumentsContract.GetTreeDocumentId(tree) : null;
        }
        catch (Java.Lang.Exception)
        {
            return null;
        }
    }

    /// <summary>Internal storage and SD cards, whose folder grants are the ones moves (and Android 10 deletes) can use.</summary>
    private const string ExternalStorageAuthority = "com.android.externalstorage.documents";

    /// <summary>Tree document ids on internal storage start with this.</summary>
    private const string PrimaryVolume = "primary:";

    /// <summary>Where internal storage is mounted for this user (/storage/emulated/0).</summary>
    private static string InternalStoragePath =>
        global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath ?? "/storage/emulated/0";

    /// <summary>The folder trees this app keeps write access to, with their tree document ids.</summary>
    private static List<(AndroidUri Tree, string DocumentId)> PersistedFolderGrants()
    {
        var grants = new List<(AndroidUri, string)>();
        foreach (var permission in Resolver.PersistedUriPermissions)
        {
            if (!permission.IsWritePermission || permission.Uri is not AndroidUri tree ||
                tree.Authority != ExternalStorageAuthority || !DocumentsContract.IsTreeUri(tree))
                continue;

            var documentId = DocumentsContract.GetTreeDocumentId(tree);
            if (!string.IsNullOrEmpty(documentId))
                grants.Add((tree, documentId));
        }
        return grants;
    }

    /// <summary>A grant covering the folder at <paramref name="relativePath"/> (internal storage first), and that folder's document id.</summary>
    private static (AndroidUri Tree, string FolderDocumentId)? FindFolderGrant(
        List<(AndroidUri Tree, string DocumentId)> grants, string relativePath)
    {
        foreach (var (tree, documentId) in grants.OrderBy(g => g.DocumentId.StartsWith(PrimaryVolume, StringComparison.Ordinal) ? 0 : 1))
        {
            if (MoveRouting.FolderDocumentId(documentId, relativePath) is { } folder)
                return (tree, folder);
        }
        return null;
    }

    /// <summary>
    /// Copies the item into a granted folder tree of the device's storage provider —
    /// <paramref name="folderDocumentId"/> inside it, or its top — and removes the original (one
    /// consent dialog). The copy is written under a hidden temporary name and named as the item
    /// only once checked (<see cref="CopyVerified"/>): a copy cut short (the app killed, the phone
    /// off) must never pass for the photo. Moved results carry the copy's new MediaStore id.
    /// </summary>
    private async Task<MoveResult> MoveViaSafAsync(MoveJob job, AndroidUri treeUri, string? folderDocumentId)
    {
        if (!CanRemoveOriginals)
            return MoveResult.Failed(OriginalWouldStay(job));

        var source = job.Source;
        if (string.IsNullOrEmpty(source.DisplayName))
            return MoveResult.Failed(CouldNotWriteInto(job.Album));

        var copySource = await OriginalForCopyAsync(job);
        if (copySource.Uri is not { } original)
            return copySource.Refusal ?? MoveResult.Failed(LocationWouldBeLost(job));

        AndroidUri? document = null;
        CopyReport copied;
        try
        {
            copied = await Task.Run(() =>
            {
                var parentId = folderDocumentId ?? DocumentsContract.GetTreeDocumentId(treeUri)!;
                var parent = DocumentsContract.BuildDocumentUriUsingTree(treeUri, parentId)
                             ?? throw new IOException("The folder is gone.");
                RemoveLeftoverTemporaries(treeUri, parentId);
                document = DocumentsContract.CreateDocument(Resolver, parent, "application/octet-stream",
                               TemporaryNames.New())
                           ?? throw new IOException("The folder refused a new file.");

                var report = CopyVerified(original, document, source.SizeBytes, job.Feedback?.Progress);

                document = DocumentsContract.RenameDocument(Resolver, document, source.DisplayName)
                           ?? throw new IOException("The copy could not be named.");
                return report;
            });
        }
        catch (Exception ex)
        {
            await Task.Run(() => TryDeleteDocument(document)); // a partial copy is worse than none
            return MoveResult.Failed(CouldNotWriteInto(job.Album, ex)); // grant revoked, folder gone, disk full
        }

        var named = document!;
        var (media, newId) = await IndexSafCopyAsync(named, source);
        if (source.IsFavorite && media is not null)
            await KeepFavoriteAsync(media, job.Kind);

        bool dated = await Task.Run(() => media is not null
            ? KeepDate(media, source)
            : newId is not null && IsDated(UriFor(newId), DateOf(source)));
        if (!dated && !await ConfirmRedatingAsync(job))
        {
            await Task.Run(() => TryDeleteDocument(named));
            return MoveResult.Cancelled;
        }

        if (newId is not null)
        {
            lock (_kindCache)
                _kindCache[newId] = job.Kind;
        }
        return await FinishMoveAsync(job, copied, newId, () => TryDeleteDocument(named));
    }

    /// <summary>
    /// Deletes hidden copies an earlier move into this folder left when the app was stopped
    /// mid-copy (best effort): only files named exactly as <see cref="TemporaryNames"/> makes
    /// them, never a folder (the provider would delete it with everything inside). Blocking.
    /// </summary>
    private static void RemoveLeftoverTemporaries(AndroidUri treeUri, string folderDocumentId)
    {
        try
        {
            var children = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, folderDocumentId);
            if (children is null)
                return;
            // Only ones untouched for an hour: a move from another screen may be writing a newer one.
            long olderThanMs = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
            using var cursor = Resolver.Query(children,
                ["document_id", "_display_name", "last_modified", "mime_type"], null, null, null);
            while (cursor is not null && cursor.MoveToNext())
            {
                var name = cursor.IsNull(1) ? null : cursor.GetString(1);
                if (!TemporaryNames.IsTemporary(name) || cursor.GetString(0) is not { } documentId)
                    continue;
                if (cursor.IsNull(3) || cursor.GetString(3) == DocumentsContract.Document.MimeTypeDir)
                    continue;
                if (cursor.IsNull(2) || cursor.GetLong(2) > olderThanMs)
                    continue;
                TryDeleteDocument(DocumentsContract.BuildDocumentUriUsingTree(treeUri, documentId));
            }
        }
        catch (Java.Lang.Exception)
        {
            // only tidying
        }
    }

    /// <summary>
    /// Gets a copy made through a folder grant into MediaStore, with the original's file time where
    /// Android allows it: has the media scanner index it (Android 10 doesn't on its own; later
    /// versions re-read it), then gets its MediaStore uri, which carries the app's grant on the
    /// document over (see <see cref="MediaUriOf"/>). Returns the uri to update the copy through,
    /// and its id; either is null when Android can't say.
    /// </summary>
    private static async Task<(AndroidUri? Media, string? Id)> IndexSafCopyAsync(AndroidUri document, SourceInfo source)
    {
        var media = await Task.Run(() => MediaUriOf(document)); // indexed as written, from Android 11
        var path = PathOfDocument(document);
        AndroidUri? scanned = null;
        if (path is not null)
        {
            if (source.ModifiedSec > 0)
                await Task.Run(() => TrySetFileTime(path, source.ModifiedSec));
            scanned = await ScanAsync(path, source.MimeType);
        }
        media ??= await Task.Run(() => MediaUriOf(document)); // Android 10: indexed by the scan
        media ??= scanned;
        var id = IdOf(media) ?? IdOf(scanned) ?? (path is null ? null : await Task.Run(() => IdByPath(path)));
        return (media, id);
    }

    /// <summary>The MediaStore id of the file at <paramref name="path"/>, if the app can see it. Blocking.</summary>
    private static string? IdByPath(string path)
    {
        try
        {
            using var cursor = Resolver.Query(FilesUri, [MediaStore.Images.Media.InterfaceConsts.Id], "_data = ?", [path], null);
            return cursor is not null && cursor.MoveToFirst() ? cursor.GetLong(0).ToString() : null;
        }
        catch (Java.Lang.Exception)
        {
            return null;
        }
    }

    /// <summary>The file of a document of the device's storage provider ("primary:Foo/x.jpg" is /storage/emulated/0/Foo/x.jpg).</summary>
    private static string? PathOfDocument(AndroidUri document)
    {
        try
        {
            var parts = (DocumentsContract.GetDocumentId(document) ?? "").Split(':', 2);
            if (parts.Length < 2)
                return null;
            var root = parts[0] == "primary" ? InternalStoragePath : $"/storage/{parts[0]}";
            return $"{root}/{parts[1]}";
        }
        catch (Java.Lang.Exception)
        {
            return null;
        }
    }

    /// <summary>Has the media scanner (re)index a file; its MediaStore uri, or null after 10 s or on failure.</summary>
    private static async Task<AndroidUri?> ScanAsync(string path, string mimeType)
    {
        try
        {
            var done = new TaskCompletionSource<AndroidUri?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Android.Media.MediaScannerConnection.ScanFile(
                Application.Context, [path], [mimeType], new ScanCompletedListener(done));
            return await Task.WhenAny(done.Task, Task.Delay(10_000)) == done.Task ? done.Task.Result : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? IdOf(AndroidUri? mediaUri)
    {
        try
        {
            long id = mediaUri is null ? -1 : ContentUris.ParseId(mediaUri);
            return id >= 0 ? id.ToString() : null;
        }
        catch (Java.Lang.Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The MediaStore uri of a document in a granted tree. Asking for it carries the app's write
    /// grant on the document over to the returned uri, which may be a files-table one. Blocking.
    /// </summary>
    private static AndroidUri? MediaUriOf(AndroidUri document)
    {
        try
        {
            return MediaStore.GetMediaUri(Application.Context, document);
        }
        catch (Java.Lang.Exception)
        {
            return null; // not indexed (yet), or the provider can't say
        }
    }

    private static void TryDeleteDocument(AndroidUri? document)
    {
        if (document is null)
            return;
        try
        {
            DocumentsContract.DeleteDocument(Resolver, document);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>
    /// Puts the ⭐ back on a copy made through a folder grant (Android 11+). The copy belongs to
    /// the system's storage provider, not to this app, so MediaStore refuses a plain update.
    /// <paramref name="media"/> comes from <see cref="MediaUriOf"/>, which carried the app's
    /// write grant on the document over to it, so the app can set the flag without asking.
    /// Only if a device still refuses does the system's favourite request run, once. Nothing
    /// here may throw: the copy is made, and only the ⭐ would be lost.
    /// </summary>
    private static async Task KeepFavoriteAsync(AndroidUri media, MediaKind kind)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
            return; // no favourites before Android 11

        try
        {
            var values = new ContentValues();
            values.Put(ColIsFavorite, 1);
            if (await Task.Run(() => Resolver.Update(media, values, null, null)) > 0)
                return;
        }
        catch (Java.Lang.Exception)
        {
            // refused — fall through to asking
        }

        try
        {
            // Requests take typed media uris only ("All requested items must be Media items").
            var typed = ContentUris.WithAppendedId(TypedCollectionFor(kind), ContentUris.ParseId(media));
            var pending = MediaStore.CreateFavoriteRequest(Resolver, new List<AndroidUri> { typed }, true);
            await IntentSenderLauncher.LaunchAsync(pending.IntentSender);
        }
        catch
        {
            // the copy stands; only the ⭐ is lost
        }
    }

    private sealed class ScanCompletedListener(TaskCompletionSource<AndroidUri?> tcs)
        : Java.Lang.Object, Android.Media.MediaScannerConnection.IOnScanCompletedListener
    {
        public void OnScanCompleted(string? path, AndroidUri? uri) => tcs.TrySetResult(uri);
    }

    /// <summary>Files-table item uri — fine for thumbnails and metadata, NOT for trash/write requests.</summary>
    private static AndroidUri UriFor(string photoId) =>
        ContentUris.WithAppendedId(FilesUri, long.Parse(photoId));

    private static AndroidUri TypedUriFor(string photoId, MediaKind kind) =>
        ContentUris.WithAppendedId(
            kind == MediaKind.Video
                ? MediaStore.Video.Media.ExternalContentUri!
                : MediaStore.Images.Media.ExternalContentUri!,
            long.Parse(photoId));

    private async Task<List<AndroidUri>> ResolveTypedUrisAsync(IReadOnlyList<string> photoIds)
    {
        List<string> missing;
        lock (_kindCache)
            missing = photoIds.Where(id => !_kindCache.ContainsKey(id)).ToList();

        if (missing.Count > 0)
            await Task.Run(() => QueryKindsIntoCache(missing));

        lock (_kindCache)
            return photoIds
                .Select(id => TypedUriFor(id, _kindCache.GetValueOrDefault(id, MediaKind.Image)))
                .ToList();
    }

    private void QueryKindsIntoCache(List<string> photoIds)
    {
        try
        {
            // ids are our own MediaStore longs; parse to sanitize before inlining.
            var idList = string.Join(',', photoIds.Select(long.Parse));
            var args = new Bundle();
            args.PutString(ContentResolver.QueryArgSqlSelection, $"_id IN ({idList})");
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
                args.PutInt(MediaStore.QueryArgMatchTrashed, 1); // MATCH_INCLUDE — restore path needs trashed rows

            using var cursor = Resolver.Query(FilesUri, ["_id", ColMediaType], args, null);
            if (cursor is null)
                return;

            while (cursor.MoveToNext())
            {
                var id = cursor.GetLong(0).ToString();
                var kind = cursor.GetInt(1) == MediaTypeVideo ? MediaKind.Video : MediaKind.Image;
                lock (_kindCache)
                    _kindCache[id] = kind;
            }
        }
        catch
        {
            // unresolved ids fall back to the image uri
        }
    }
}
