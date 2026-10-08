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
            await gallery.GetPhotosAsync(); // warm the cache so thumbnails resolve
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
            if (!ok)
                return; // user denied the system dialog

            await gallery.Store.RemoveAsync(item.PhotoId); // unreviewed again
            CommittedItems.Remove(item);
            HasCommitted = CommittedItems.Count > 0;
            gallery.Invalidate();
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
            : $"Permanently delete {Items.Count} item(s)? Your device has no system trash — this CANNOT be undone.";

        bool confirmed = await Shell.Current.DisplayAlertAsync("Empty bin", message,
            SystemTrash ? "Move to trash" : "Delete forever", "Cancel");
        if (!confirmed)
            return;

        IsBusy = true;
        try
        {
            var ids = Items.Select(i => i.PhotoId).ToList();
            var ok = await gallery.Library.MoveToSystemTrashAsync(ids);
            if (!ok)
                return; // user denied the system dialog — everything stays in the bin

            await gallery.Store.MarkCommittedAsync(ids);
            gallery.Invalidate();
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
