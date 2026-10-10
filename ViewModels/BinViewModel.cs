using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SortAndDelete.Helpers;
using SortAndDelete.Models;
using SortAndDelete.Services;

namespace SortAndDelete.ViewModels;

public sealed class BinItem
{
    public required string PhotoId { get; init; }
    public required ImageSource Thumbnail { get; init; }
    public long SizeBytes { get; init; }
    public string SizeText => SizeBytes > 0 ? ByteFormat.Human(SizeBytes) : "";

    /// <summary>The storage volume it was on when it went to the bin; null when that wasn't recorded.</summary>
    public string? VolumeName { get; init; }
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
                    VolumeName = record.VolumeName,
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
                        VolumeName = record.VolumeName,
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

    // Not while the bin is being emptied (or a restore from the system trash is under way): the
    // request has its list of items already, and one restored now would be removed all the same,
    // with nothing left to say where it went.
    [RelayCommand]
    private async Task Restore(BinItem? item)
    {
        if (item is null || IsBusy)
            return;
        await gallery.Store.RemoveAsync(item.PhotoId);
        Items.Remove(item);
        UpdateSummary(Items.Sum(i => i.SizeBytes));
    }

    [RelayCommand]
    private async Task RestoreAll()
    {
        if (Items.Count == 0 || IsBusy)
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
            RestoreOutcome outcome;
            try
            {
                outcome = await gallery.Library.RestoreFromSystemTrashAsync(item.PhotoId, item.VolumeName);
            }
            catch (Exception)
            {
                outcome = RestoreOutcome.NotRestored; // nothing says it came back, so it stays listed
            }
            finally
            {
                // Whatever it reports, the library may have changed: Android's dialog can close
                // without saying what it did (a rotation can do that), and the check afterwards can
                // fail, so "not restored" doesn't mean nothing came back. The gallery reads the
                // library again from here on.
                gallery.Invalidate();
            }

            switch (outcome)
            {
                // The user denied the system dialog, or Android didn't restore it (its dialog reports
                // success either way, so the service checks the gallery): it stays listed.
                case RestoreOutcome.NotRestored:
                    return;

                // Out of reach: its card is out, so it is still in the trash there, where Android
                // purges it once the card is back; or it is outside the photos the app may see.
                // Dropping the entry would leave nothing to restore it from.
                case RestoreOutcome.Unreachable:
                    await Shell.Current.DisplayAlertAsync("Can't restore it right now",
                        await gallery.Library.HasLimitedAccessAsync()
                            ? "Sort & Delete can't see this photo: it isn't among the photos you've given it access to. " +
                              "It stays listed here: allow access to all photos, then restore it."
                            : "This photo is on a memory card that isn't in the phone, so it can't be taken out of " +
                              "the trash now. It stays listed here: put the card back, then restore it.", "OK");
                    return;
            }

            // Restored: unreviewed again. Or gone: purged from the trash, or removed elsewhere, on
            // storage that is there, so nothing is left to restore and the entry goes.
            await gallery.Store.RemoveAsync(item.PhotoId);
            CommittedItems.Remove(item);
            HasCommitted = CommittedItems.Count > 0;

            if (outcome == RestoreOutcome.Gone)
                await Shell.Current.DisplayAlertAsync("Nothing left to restore",
                    "This photo is no longer in the system trash: Android or another app has removed it for good.", "OK");
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
            var volumes = Items
                .Where(i => !string.IsNullOrEmpty(i.VolumeName))
                .ToDictionary(i => i.PhotoId, i => i.VolumeName!, StringComparer.Ordinal);

            TrashResult result;
            try
            {
                result = await gallery.Library.MoveToSystemTrashAsync(ids, volumes);
            }
            catch (Exception)
            {
                // It failed before it could say what it had done, so nothing is recorded: everything
                // stays in the bin, and emptying it again sorts it out, as a request asks only for
                // what the library still has. Left to escape the command, this would close the app
                // right after a delete.
                result = TrashResult.AllLeft(ids);
            }
            finally
            {
                // Whatever it reports, the library may have changed: a request that failed as a whole
                // can still have trashed or deleted some of them. So the list the gallery keeps is
                // dropped here, before anything reads it again: every read from now on waits for a
                // load that began after the request ended. Home finds that load when it shows next.
                gallery.Invalidate();
            }

            // Only what is out of the library and accounted for is recorded as removed, and so
            // counted as freed: in the system trash, or deleted and checked gone (Android 10). The
            // service settles that for each item from the library as the request left it. An item
            // the library merely no longer lists is not recorded: on an SD card that is out it was
            // never touched, and stays in the bin until the card is back; one that something else
            // removed is forgotten, so it doesn't sit in the bin for good, and comes back
            // unreviewed should it turn up again. Whatever the user declined, Android refused or a
            // failed request left behind stays in the bin as well.
            try
            {
                if (result.Removed.Count > 0)
                    await gallery.Store.MarkCommittedAsync(result.Removed);
                if (result.Missing.Count > 0)
                    await gallery.Store.RemoveAllAsync(result.Missing);
            }
            catch (Exception)
            {
                // The decisions couldn't be saved (a full disk, say). What went is in the system
                // trash or gone either way; the bin shows what is recorded, and emptying it again
                // settles the rest. Left to escape the command, this would close the app.
            }
            await LoadAsync();

            // What stays in the bin, or left it without being removed, is said: an emptied bin that
            // isn't empty, or is short of an item nobody deleted, would otherwise go unexplained.
            string? outOfReach = result.Unreachable.Count == 0
                ? null
                : await OutOfReachAsync(result.Unreachable.Count);
            string? alreadyGone = result.Missing.Count == 0 ? null : AlreadyGone(result.Missing.Count);
            string also = string.Concat(new[] { outOfReach, alreadyGone }.Where(part => part is not null).Select(part => " " + part));

            if (result.Left.Count > 0 && !SystemTrash)
            {
                // Android 10: the user backed out of the folder picker, or a file couldn't be deleted
                // (the others were). On Android 11+ and iOS the user denied the system dialog, or
                // Android didn't trash every item; nothing says which, so there is nothing to explain.
                await Shell.Current.DisplayAlertAsync("Not everything was deleted",
                    "You backed out of Android's folder screen, or a file couldn't be deleted. What's still " +
                    "on your device stays in the bin: empty it again to finish, and allow access to the " +
                    "folder Android opens on." + also, "OK");
            }
            else if (outOfReach is not null)
            {
                await Shell.Current.DisplayAlertAsync("Not everything could be reached", also.TrimStart(), "OK");
            }
            else if (alreadyGone is not null)
            {
                await Shell.Current.DisplayAlertAsync("Already gone", alreadyGone, "OK");
            }

#if IOS
            if (result.EmptiesBin)
                await Shell.Current.DisplayAlertAsync("Done",
                    "Photos moved to Recently Deleted. If you ever need one back within 30 days, restore it in the Photos app → Albums → Recently Deleted.", "OK");
#endif
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Says that some of the bin's items can't be reached, were left alone, and what brings them
    /// back: with access to selected photos only they are outside the selection (the library then
    /// lists nothing else, so nothing tells a card that is out from that); otherwise they are on a
    /// memory card that isn't in the phone.
    /// </summary>
    private async Task<string> OutOfReachAsync(int count)
    {
        string they = count == 1 ? "it was" : "they were";
        string stay = count == 1 ? "stays" : "stay";
        return await gallery.Library.HasLimitedAccessAsync()
            ? (count == 1 ? "1 item isn't" : $"{count:N0} items aren't") +
              $" among the photos you've given Sort & Delete access to, so {they} not touched and {stay} in the bin. " +
              "Allow access to all photos, then empty the bin again."
            : (count == 1 ? "1 item is" : $"{count:N0} items are") +
              $" on a memory card that isn't in the phone, so {they} not touched and {stay} in the bin. " +
              "Put the card back, then empty the bin again.";
    }

    /// <summary>Says that some of the bin's items had left the gallery by other means, and were only taken off the list.</summary>
    private static string AlreadyGone(int count) =>
        (count == 1 ? "1 item was" : $"{count:N0} items were") +
        " no longer in your gallery (deleted or moved by another app), so " +
        (count == 1 ? "it was" : "they were") + " only taken off the bin's list.";
}
