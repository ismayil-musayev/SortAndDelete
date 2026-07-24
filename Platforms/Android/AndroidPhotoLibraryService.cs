using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using SortAndDelete.Models;
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
        var status = await Permissions.RequestAsync<GalleryPermission>();
        if (status == PermissionStatus.Granted)
            return true;

        // Android 14+: the user may have granted access to selected photos only.
        if (OperatingSystem.IsAndroidVersionAtLeast(34) &&
            await Permissions.CheckStatusAsync<PartialGalleryPermission>() == PermissionStatus.Granted)
            return true;

        return false;
    }

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

        var uris = await ResolveTypedUrisAsync(photoIds);

        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
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

        // Android 10 fallback: no system trash — permanent delete, with the per-item
        // consent dialog Android requires for photos this app doesn't own.
        foreach (var uri in uris)
        {
            if (!await DeleteWithConsentAsync(uri))
                return false;
        }
        return true;
    }

    private static async Task<bool> DeleteWithConsentAsync(AndroidUri uri)
    {
        try
        {
            Resolver.Delete(uri, null, null);
            return true;
        }
        catch (Java.Lang.SecurityException ex)
        {
            if (ex is not RecoverableSecurityException recoverable)
                return false;

            var intentSender = recoverable.UserAction.ActionIntent.IntentSender;
            if (!await IntentSenderLauncher.LaunchAsync(intentSender))
                return false;

            try
            {
                Resolver.Delete(uri, null, null);
                return true;
            }
            catch
            {
                return false;
            }
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

    public async Task<string?> MoveToAlbumAsync(string photoId, AlbumInfo album)
    {
        // Folders picked via the system browser: MediaStore can't relocate media there,
        // so we copy the bytes and delete the original instead.
        if (album.Id.StartsWith(SafPrefix, StringComparison.Ordinal))
            return await MoveViaSafAsync(photoId, album.Id[SafPrefix.Length..]);

        var relativePath = string.IsNullOrEmpty(album.Id) ? $"Pictures/{album.Name}/" : album.Id;

        // Files inside another app's Android/media directory (WhatsApp, Telegram, …)
        // can't be relocated by MediaStore at all — copy + delete instead.
        var source = await Task.Run(() => QuerySourceInfo(photoId));
        if (source is null)
            return null;
        if (source.RelativePath.StartsWith("Android/", StringComparison.OrdinalIgnoreCase))
            return await MoveViaCopyAsync(photoId, relativePath, source);

        var uri = (await ResolveTypedUrisAsync([photoId])).First();
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                // Ask for write access to this photo (system dialog), then move it.
                var pending = MediaStore.CreateWriteRequest(Resolver, new List<AndroidUri> { uri });
                if (!await IntentSenderLauncher.LaunchAsync(pending.IntentSender))
                    return null;
            }

            if (await Task.Run(() => UpdateRelativePath(uri, relativePath)))
                return photoId; // moved in place — same id, all metadata intact

            // Some sources still refuse the in-place move — fall back to copy + delete.
            return await MoveViaCopyAsync(photoId, relativePath, source);
        }
        catch (Java.Lang.SecurityException ex)
        {
            // Android 10 path: consent is requested via RecoverableSecurityException.
            if (ex is not RecoverableSecurityException recoverable)
                return null;

            if (!await IntentSenderLauncher.LaunchAsync(recoverable.UserAction.ActionIntent.IntentSender))
                return null;

            try
            {
                return await Task.Run(() => UpdateRelativePath(uri, relativePath)) ? photoId : null;
            }
            catch
            {
                return null;
            }
        }
        catch (Java.Lang.Exception)
        {
            return await MoveViaCopyAsync(photoId, relativePath, source);
        }
        catch
        {
            return null;
        }
    }

    private sealed record SourceInfo(
        string DisplayName,
        string MimeType,
        string RelativePath,
        long TakenMs,
        long ModifiedSec,
        bool IsFavorite);

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
            hasFavorite && !cursor.IsNull(5) && cursor.GetInt(5) == 1);
    }

    /// <summary>
    /// Move by copying through MediaStore (new row we own, no consent needed) and then
    /// deleting the original (one consent dialog). Rolls the copy back if the user denies.
    /// Bytes are copied verbatim, so in-file metadata (EXIF date, GPS location, camera)
    /// survives; the taken-date, file time and favorite flag are carried over explicitly.
    /// Returns the NEW item's id, or null on failure.
    /// </summary>
    private async Task<string?> MoveViaCopyAsync(string photoId, string targetRelativePath, SourceInfo source)
    {
        if (string.IsNullOrEmpty(source.DisplayName))
            return null;

        bool isVideo;
        lock (_kindCache)
            isVideo = _kindCache.GetValueOrDefault(photoId) == MediaKind.Video;
        var targetCollection = isVideo
            ? MediaStore.Video.Media.ExternalContentUri!
            : MediaStore.Images.Media.ExternalContentUri!;

        AndroidUri? newUri = null;
        try
        {
            var values = new ContentValues();
            values.Put(MediaStore.Images.Media.InterfaceConsts.DisplayName, source.DisplayName);
            values.Put(MediaStore.Images.Media.InterfaceConsts.MimeType, source.MimeType);
            values.Put(MediaStore.Images.Media.InterfaceConsts.RelativePath, targetRelativePath);
            if (source.TakenMs > 0)
                values.Put(MediaStore.Images.Media.InterfaceConsts.DateTaken, source.TakenMs);
            values.Put("is_pending", 1);

            newUri = Resolver.Insert(targetCollection, values);
            if (newUri is null)
                return null;

            using (var input = Resolver.OpenInputStream(UriFor(photoId)))
            using (var output = Resolver.OpenOutputStream(newUri))
            {
                if (input is null || output is null)
                    throw new IOException("Could not open streams for copy.");
                await input.CopyToAsync(output);
                await output.FlushAsync();
            }

            var publish = new ContentValues();
            publish.Put("is_pending", 0);
            if (source.IsFavorite && OperatingSystem.IsAndroidVersionAtLeast(30))
                publish.Put(ColIsFavorite, 1); // keep the ⭐ flag on the copy
            Resolver.Update(newUri, publish, null, null);

            // Preserve the file's modified time so EXIF-less items keep their month.
            PreserveFileTime(newUri, source.ModifiedSec);
        }
        catch
        {
            TryDeleteOwnCopy(newUri);
            return null;
        }

        // Delete the original — this is a move, not a copy.
        bool deleted;
        var typedUris = await ResolveTypedUrisAsync([photoId]);
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            try
            {
                var pending = MediaStore.CreateDeleteRequest(Resolver, typedUris);
                deleted = await IntentSenderLauncher.LaunchAsync(pending.IntentSender);
            }
            catch (Java.Lang.Exception)
            {
                deleted = false;
            }
        }
        else
        {
            deleted = await DeleteWithConsentAsync(typedUris.First());
        }

        if (!deleted)
        {
            TryDeleteOwnCopy(newUri); // user backed out — don't leave a duplicate behind
            return null;
        }

        var newId = ContentUris.ParseId(newUri).ToString();
        lock (_kindCache)
            _kindCache[newId] = isVideo ? MediaKind.Video : MediaKind.Image;
        return newId;
    }

    /// <summary>Sets the new (own) file's mtime to the source's and refreshes its MediaStore row.</summary>
    private void PreserveFileTime(AndroidUri ownUri, long modifiedSec)
    {
        if (modifiedSec <= 0)
            return;
        try
        {
            string? path = null;
            using (var cursor = Resolver.Query(ownUri, ["_data"], null, null, null))
            {
                if (cursor is not null && cursor.MoveToFirst() && !cursor.IsNull(0))
                    path = cursor.GetString(0);
            }
            if (path is null)
                return;

            var file = new Java.IO.File(path);
            if (file.SetLastModified(modifiedSec * 1000L))
            {
                Android.Media.MediaScannerConnection.ScanFile(
                    Application.Context, [path], null, null); // refresh date_modified in the index
            }
        }
        catch
        {
            // cosmetic — the copy still succeeded
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

    public Task<AlbumInfo?> CreateAlbumAsync(string name)
    {
        // On Android an album is just a folder — it comes into existence with the first
        // photo moved into it, so nothing to create up front.
        var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (string.IsNullOrWhiteSpace(safe))
            return Task.FromResult<AlbumInfo?>(null);

        return Task.FromResult<AlbumInfo?>(new AlbumInfo
        {
            Id = $"Pictures/{safe}/",
            Name = safe,
            Count = 0,
            Subtitle = $"Pictures/{safe}/",
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

    public async Task<AlbumInfo?> PickExternalFolderAsync()
    {
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission
                        | ActivityFlags.GrantWriteUriPermission
                        | ActivityFlags.GrantPersistableUriPermission);

        var result = await ActivityLauncher.LaunchForResultAsync(intent);
        if (result?.Data is not AndroidUri treeUri)
            return null;

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
        return SafAlbumFor(treeUri.ToString()!);
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

    private async Task<string?> MoveViaSafAsync(string photoId, string treeUriString)
    {
        var treeUri = AndroidUri.Parse(treeUriString);
        if (treeUri is null)
            return null;

        var source = await Task.Run(() => QuerySourceInfo(photoId));
        if (source is null || string.IsNullOrEmpty(source.DisplayName))
            return null;

        // Copy the bytes into the picked folder
        AndroidUri? newDoc;
        try
        {
            var parentDoc = DocumentsContract.BuildDocumentUriUsingTree(
                treeUri, DocumentsContract.GetTreeDocumentId(treeUri)!);
            newDoc = DocumentsContract.CreateDocument(Resolver, parentDoc!, source.MimeType, source.DisplayName);
            if (newDoc is null)
                return null;

            using var input = Resolver.OpenInputStream(UriFor(photoId));
            using var output = Resolver.OpenOutputStream(newDoc);
            if (input is null || output is null)
                return null;
            await input.CopyToAsync(output);
            await output.FlushAsync();
        }
        catch
        {
            return null; // grant revoked or target not writable
        }

        // Delete the original (one consent dialog) — this is a move, not a copy.
        bool deleted;
        var typedUris = await ResolveTypedUrisAsync([photoId]);
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            try
            {
                var pending = MediaStore.CreateDeleteRequest(Resolver, typedUris);
                deleted = await IntentSenderLauncher.LaunchAsync(pending.IntentSender);
            }
            catch (Java.Lang.Exception)
            {
                deleted = false;
            }
        }
        else
        {
            deleted = await DeleteWithConsentAsync(typedUris.First());
        }
        if (!deleted)
        {
            try
            {
                DocumentsContract.DeleteDocument(Resolver, newDoc); // roll the copy back
            }
            catch
            {
                // best effort
            }
            return null;
        }

        // Resolve the copy's new MediaStore id so the caller can mark it reviewed.
        return await ResolveSafCopyIdAsync(newDoc, source) ?? photoId;
    }

    /// <summary>
    /// Derives the on-disk path of a document we just created ("primary:Foo" →
    /// /storage/emulated/0/Foo/…), preserves the file time when possible, and asks the
    /// media scanner to index it, returning the resulting MediaStore id.
    /// </summary>
    private static async Task<string?> ResolveSafCopyIdAsync(AndroidUri newDoc, SourceInfo source)
    {
        try
        {
            var docId = DocumentsContract.GetDocumentId(newDoc) ?? "";
            var parts = docId.Split(':', 2);
            if (parts.Length < 2)
                return null;
            var root = parts[0] == "primary" ? "/storage/emulated/0" : $"/storage/{parts[0]}";
            var path = $"{root}/{parts[1]}";

            if (source.ModifiedSec > 0)
            {
                try
                {
                    new Java.IO.File(path).SetLastModified(source.ModifiedSec * 1000L);
                }
                catch
                {
                    // not always permitted outside media collections — cosmetic
                }
            }

            var tcs = new TaskCompletionSource<AndroidUri?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Android.Media.MediaScannerConnection.ScanFile(
                Application.Context, [path], [source.MimeType], new ScanCompletedListener(tcs));

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(4000));
            if (completed != tcs.Task || tcs.Task.Result is not AndroidUri scanned)
                return null;
            return ContentUris.ParseId(scanned).ToString();
        }
        catch
        {
            return null;
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
