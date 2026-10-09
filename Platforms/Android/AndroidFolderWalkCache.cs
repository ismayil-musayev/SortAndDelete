using Microsoft.Maui.ApplicationModel;
using SortAndDelete.Logic;

namespace SortAndDelete.Services;

/// <summary>
/// The folders on disk that <see cref="AndroidPhotoLibraryService.GetAlbumsAsync"/> finds by walking
/// Pictures/, DCIM/ and Movies/, the move picker's empty folders among them. They are kept while
/// the app is in the foreground, and dropped whenever an activity stops (the app goes to the
/// background, or a system screen such as the folder picker opens over it), as other apps can
/// change folders meanwhile. A system dialog over the app only pauses the activity, and keeps them.
/// Folders with something in them come from MediaStore on every list, so the cache only delays
/// empty folders: one made after the walk is listed after the next stop, whether another app made
/// it or it is an album that got a photo and was emptied again in the same foreground session,
/// and one deleted after the walk stays listed until then.
/// </summary>
internal static class AndroidFolderWalkCache
{
    public static FolderWalkCache Shared { get; } = new();

    static AndroidFolderWalkCache() =>
        ActivityStateManager.Default.ActivityStateChanged += (_, e) =>
        {
            if (e.State == ActivityState.Stopped)
                Shared.Invalidate();
        };
}
