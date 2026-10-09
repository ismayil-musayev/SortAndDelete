namespace SortAndDelete.Services;

/// <summary>
/// READ_MEDIA_IMAGES + READ_MEDIA_VIDEO on Android 13+, READ_EXTERNAL_STORAGE before that.
/// </summary>
public sealed class GalleryPermission : Permissions.BasePlatformPermission
{
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
        OperatingSystem.IsAndroidVersionAtLeast(33)
            ?
            [
                (global::Android.Manifest.Permission.ReadMediaImages, true),
                (global::Android.Manifest.Permission.ReadMediaVideo, true),
            ]
            : [(global::Android.Manifest.Permission.ReadExternalStorage, true)];
}

/// <summary>
/// Android 14+ "allow selected photos" partial access. When the user picks this,
/// READ_MEDIA_IMAGES is denied but the app can still work with the selected subset.
/// </summary>
public sealed class PartialGalleryPermission : Permissions.BasePlatformPermission
{
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
        [("android.permission.READ_MEDIA_VISUAL_USER_SELECTED", true)];
}

/// <summary>
/// ACCESS_MEDIA_LOCATION: without it Android blanks the location in every photo or video the
/// app reads but didn't create, so a move that copies the file would lose where it was taken.
/// Kept apart from <see cref="GalleryPermission"/>: missing it must not make the library look
/// inaccessible.
/// </summary>
public sealed class MediaLocationPermission : Permissions.BasePlatformPermission
{
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
        [(global::Android.Manifest.Permission.AccessMediaLocation, true)];
}
