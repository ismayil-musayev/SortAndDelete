using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SortAndDelete.Helpers;
using SortAndDelete.Services;

namespace SortAndDelete.ViewModels;

public sealed class BinItem
{
    public required string PhotoId { get; init; }
    public required ImageSource Thumbnail { get; init; }
    public long SizeBytes { get; init; }
    public string SizeText => SizeBytes > 0 ? ByteFormat.Human(SizeBytes) : "";
}

public partial class BinViewModel(GalleryService gallery) : ObservableObject
{
    public ObservableCollection<BinItem> Items { get; } = [];

    /// <summary>Android only: photos already in the system trash, still restorable for ~30 days.</summary>
    public ObservableCollection<BinItem> CommittedItems { get; } = [];

    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool isEmpty = true;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string summaryText = "";
    [ObservableProperty] private string emptyButtonText = "Empty bin";
    [ObservableProperty] private bool hasCommitted;

    private bool SystemTrash => gallery.Library.SupportsSystemTrash;

    public string SafetyHint => SystemTrash
        ? "Photos here are untouched until you empty the bin. Emptying moves them to the system trash, where they are permanently deleted after ~30 days."
        : "Photos here are untouched until you empty the bin. Your Android version has no system trash — emptying deletes permanently.";

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var pending = await gallery.Store.GetPendingTrashAsync();

            Items.Clear();
            long totalBytes = 0;
            foreach (var record in pending.OrderByDescending(r => r.DecidedAt))
            {
                totalBytes += record.SizeBytes;
                Items.Add(new BinItem
                {
                    PhotoId = record.PhotoId,
                    Thumbnail = gallery.CreateThumbnail(record.PhotoId, 320),
                    SizeBytes = record.SizeBytes,
                });
            }

            UpdateSummary(totalBytes);

            // Recently emptied (Android): rows survive in MediaStore while trashed, so we
            // can offer restore from inside the app for the 30-day window.
            CommittedItems.Clear();
            if (gallery.Library.CanRestoreFromSystemTrash)
            {
                var committed = await gallery.Store.GetCommittedTrashSinceAsync(DateTime.UtcNow.AddDays(-30));
                foreach (var record in committed.OrderByDescending(r => r.CommittedAt))
                {
                    CommittedItems.Add(new BinItem
                    {
                        PhotoId = record.PhotoId,
                        Thumbnail = gallery.CreateThumbnail(record.PhotoId, 320),
                        SizeBytes = record.SizeBytes,
                    });
                }
            }
            HasCommitted = CommittedItems.Count > 0;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void UpdateSummary(long totalBytes)
    {
        IsEmpty = Items.Count == 0;
        SummaryText = IsEmpty
            ? ""
            : $"{Items.Count:N0} item{(Items.Count == 1 ? "" : "s")} · {ByteFormat.Human(totalBytes)}";
        EmptyButtonText = IsEmpty
            ? "Empty bin"
            : $"Empty bin · free {ByteFormat.Human(totalBytes)}";
    }

    [RelayCommand]
    private async Task Restore(BinItem? item)
    {
        if (item is null)
            return;
        await gallery.Store.RemoveAsync(item.PhotoId);
        Items.Remove(item);
        UpdateSummary(Items.Sum(i => i.SizeBytes));
    }

    [RelayCommand]
    private async Task RestoreAll()
    {
        if (Items.Count == 0)
            return;
        foreach (var item in Items.ToList())
            await gallery.Store.RemoveAsync(item.PhotoId);
        Items.Clear();
        UpdateSummary(0);
    }

    /// <summary>Pulls a photo back out of the Android system trash.</summary>
    [RelayCommand]
    private async Task RestoreCommitted(BinItem? item)
    {
        if (item is null || IsBusy)
            return;

        IsBusy = true;
        try
        {
            var ok = await gallery.Library.RestoreFromSystemTrashAsync([item.PhotoId]);

            // Whatever it reports, the library may have changed: Android's dialog can close without
            // saying what it did (a rotation can do that), and the check afterwards can fail, so
            // false doesn't mean nothing came back. The gallery reads the library again from here on.
            gallery.Invalidate();

            // False: the user denied the system dialog, or Android didn't restore it (its dialog reports
            // success either way, so the service checks the gallery); it stays listed. True also when
            // nothing was left to restore (purged from the trash elsewhere, or on a card that isn't
            // mounted), so the entry goes.
            if (!ok)
                return;

            await gallery.Store.RemoveAsync(item.PhotoId); // unreviewed again
            CommittedItems.Remove(item);
            HasCommitted = CommittedItems.Count > 0;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Empty()
    {
        if (Items.Count == 0 || IsBusy)
            return;

        string message = SystemTrash
            ? $"Move {Items.Count} item(s) to the system trash? They will be permanently deleted by the system after ~30 days. Until then you can still get them back from the system trash (Android) or Photos → Recently Deleted (iOS)."
            : $"Permanently delete {Items.Count} item(s)? Your device has no system trash — this CANNOT be undone. " +
              "Android may ask you for access to the folders they are in, once per folder: on the next screen, " +
              "allow access to the folder it opens on.";

        bool confirmed = await Shell.Current.DisplayAlertAsync("Empty bin", message,
            SystemTrash ? "Move to trash" : "Delete forever", "Cancel");
        if (!confirmed)
            return;

        IsBusy = true;
        try
        {
            var ids = Items.Select(i => i.PhotoId).ToList();
            var ok = await gallery.Library.MoveToSystemTrashAsync(ids);

            // Whatever it reports, the library may have changed: a request that failed as a whole can
            // still have trashed or deleted some of them (see below). So the list the gallery keeps is
            // dropped here, before anything reads it again: every read from now on waits for a load
            // that began after the request ended.
            gallery.Invalidate();
            if (!ok)
            {
                // Not every item went. Android 11+: the user denied the system dialog, Android didn't
                // trash every item (its dialog reports success either way, so the service checks the
                // gallery), or the dialog closed without reporting what it had done (a rotation can do
                // that); nothing here says which, so nothing to explain. Android 10: the user backed out
                // of the folder picker, or a file couldn't be deleted (the others were). Either way some
                // may have gone, and an item left pending after it went would only be forgotten by ↺
                // while Android purges it. So what the library no longer has (in the system trash,
                // deleted, or otherwise gone) is committed, and the rest stays in the bin. iOS: the user
                // denied the dialog, so nothing left the library and nothing changes.
#if ANDROID
                try
                {
                    // The library as the request left it: this read comes after the invalidation
                    // above, so it never gets the list kept from before the request, which still has
                    // every item and would leave the ones that went pending. Home finds this load
                    // when it shows next.
                    var library = await gallery.GetPhotosAsync();
                    var gone = ids.Except(library.Select(p => p.Id)).ToList();
                    if (gone.Count > 0)
                        await gallery.Store.MarkCommittedAsync(gone);
                }
                catch (Exception)
                {
                    // The library can't be read just now, so nothing says what went. Everything stays
                    // in the bin, and emptying it again sorts it out: a request asks only for what the
                    // library still has. Left to escape the command, this would close the app right
                    // after a delete.
                }
                await LoadAsync();
#endif
                if (!SystemTrash)
                    await Shell.Current.DisplayAlertAsync("Not everything was deleted",
                        "You backed out of Android's folder screen, or a file couldn't be deleted. What's still " +
                        "on your device stays in the bin: empty it again to finish, and allow access to the " +
                        "folder Android opens on.", "OK");
                return;
            }

            await gallery.Store.MarkCommittedAsync(ids);
            await LoadAsync();

#if IOS
            await Shell.Current.DisplayAlertAsync("Done",
                "Photos moved to Recently Deleted. If you ever need one back within 30 days, restore it in the Photos app → Albums → Recently Deleted.", "OK");
#endif
        }
        finally
        {
            IsBusy = false;
        }
    }
}
