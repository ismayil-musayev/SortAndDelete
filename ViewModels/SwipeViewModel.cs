using CommunityToolkit.Mvvm.ComponentModel;
using SortAndDelete.Logic;
using SortAndDelete.Models;
using SortAndDelete.Services;

namespace SortAndDelete.ViewModels;

[QueryProperty(nameof(MonthKey), "month")]
[QueryProperty(nameof(ModeName), "mode")]
[QueryProperty(nameof(FolderId), "folder")]
public partial class SwipeViewModel(GalleryService gallery, IAlbumPickerService albumPicker) : ObservableObject
{
    private const int CardPixelSize = 2048;

    private List<PhotoAsset> _queue = [];
    private readonly Stack<(PhotoAsset Photo, SwipeDecision Decision)> _history = new();
    private int _total;
    private bool _initialized;

    public DeckMode Mode { get; private set; } = DeckMode.Month;

    [ObservableProperty] private string monthKey = "";
    [ObservableProperty] private string modeName = "";
    [ObservableProperty] private string folderId = "";
    [ObservableProperty] private string title = "";
    [ObservableProperty] private ImageSource? currentImage;
    [ObservableProperty] private ImageSource? nextImage;
    [ObservableProperty] private string positionText = "";
    [ObservableProperty] private string currentDateText = "";
    [ObservableProperty] private string currentLocationText = "";
    [ObservableProperty] private string currentSizeText = "";
    [ObservableProperty] private bool isMonthMode;
    [ObservableProperty] private bool isCurrentVideo;
    [ObservableProperty] private string currentDurationText = "";
    [ObservableProperty] private double progress;
    [ObservableProperty] private bool isDone;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool canUndo;
    [ObservableProperty] private string binBadgeText = "Bin";
    [ObservableProperty] private string doneTitle = "All done!";
    [ObservableProperty] private string doneSubtitle = "";

    public PhotoAsset? CurrentPhoto => _queue.Count > 0 ? _queue[0] : null;

    /// <summary>Whether the ↗ open-in-gallery button is shown (Android only).</summary>
    public bool CanOpenExternally => gallery.Library.SupportsOpenExternally;

    /// <summary>Hands the current item to the default viewer app (or the system chooser).</summary>
    public async Task OpenExternallyAsync()
    {
        var photo = CurrentPhoto;
        if (photo is null)
            return;

        if (!await gallery.Library.OpenExternallyAsync(photo.Id))
            await Shell.Current.DisplayAlertAsync("Can't open",
                "No app on this device can open this file.", "OK");
    }

    /// <summary>First load of the deck queue (skips photos already reviewed — resume for free).</summary>
    public async Task InitAsync()
    {
        if (_initialized)
        {
            await ResyncAsync();
            return;
        }

        IsLoading = true;
        try
        {
            Mode = ModeName.ToLowerInvariant() switch
            {
                "biggest" => DeckMode.Biggest,
                _ => DeckMode.Month,
            };
            IsMonthMode = Mode == DeckMode.Month;

            (Title, DoneTitle, DoneSubtitle) = Mode switch
            {
                DeckMode.Biggest => ("Biggest files", "Deck cleared! 🧹", "Nothing unreviewed left in your library."),
                _ => (MonthGroup.DisplayFor(MonthKey), "Month cleaned!", $"Nothing left to review in {MonthGroup.DisplayFor(MonthKey)}."),
            };

            if (Mode == DeckMode.Month)
                Preferences.Default.Set("last_month_key", MonthKey);

            await LoadQueueAsync();
            _initialized = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private string? Folder => string.IsNullOrEmpty(FolderId) ? null : FolderId;

    /// <summary>Fresh queue load — used by init and by "Re-review month".</summary>
    private async Task LoadQueueAsync()
    {
        _queue = await gallery.GetQueueAsync(Mode, MonthKey, Folder);
        _total = Mode == DeckMode.Month
            ? (await gallery.GetPhotosAsync(Folder)).Count(p => p.MonthKey == MonthKey)
            : _queue.Count;

        _history.Clear();
        await RefreshBinBadgeAsync();
        RefreshCards();
    }

    /// <summary>
    /// Re-entry after visiting the bin or album picker: drop photos reviewed elsewhere,
    /// put restored photos back at the front, keep the rest of the queue order intact.
    /// </summary>
    private async Task ResyncAsync()
    {
        var fresh = await gallery.GetQueueAsync(Mode, MonthKey, Folder);
        var freshIds = fresh.Select(p => p.Id).ToHashSet();
        var knownIds = _queue.Select(p => p.Id).ToHashSet();

        _queue = [.. fresh.Where(p => !knownIds.Contains(p.Id)), .. _queue.Where(p => freshIds.Contains(p.Id))];

        if (Mode == DeckMode.Month)
            _total = (await gallery.GetPhotosAsync(Folder)).Count(p => p.MonthKey == MonthKey);
        // The Biggest deck keeps the total fixed from init; RefreshCards clamps if restores grow the queue.

        await RefreshBinBadgeAsync();
        RefreshCards();
    }

    /// <summary>
    /// Forgets this month's Keep/Moved decisions and deals the deck again.
    /// Photos in the bin (or already in the system trash) are not touched.
    /// </summary>
    public async Task ReReviewAsync()
    {
        if (Mode != DeckMode.Month)
            return;

        bool confirmed = await Shell.Current.DisplayAlertAsync("Re-review month",
            $"Review {Title} again from the start? Your keep decisions for this month are forgotten. Photos in the bin stay in the bin.",
            "Re-review", "Cancel");
        if (!confirmed)
            return;

        await gallery.Store.ClearKeepDecisionsForMonthAsync(MonthKey);
        await LoadQueueAsync();
    }

    private async Task RefreshBinBadgeAsync()
    {
        var pending = await gallery.Store.GetPendingTrashAsync();
        BinBadgeText = pending.Count == 0 ? "Bin" : $"Bin · {pending.Count}";
    }

    private void RefreshCards()
    {
        var current = CurrentPhoto;
        var next = _queue.Count > 1 ? _queue[1] : null;

        CurrentImage = current is null ? null : gallery.CreateThumbnail(current.Id, CardPixelSize);
        NextImage = next is null ? null : gallery.CreateThumbnail(next.Id, CardPixelSize);

        IsDone = current is null;
        int done = Math.Max(0, _total - _queue.Count);
        PositionText = IsDone ? "" : $"{done + 1} / {_total}";
        Progress = _total == 0 ? 1 : (double)done / _total;
        CurrentDateText = current?.TakenAt.ToString("d MMM yyyy · HH:mm") ?? "";
        CurrentLocationText = current?.LocationText ?? "";
        CurrentSizeText = current is { SizeBytes: > 0 }
            ? Helpers.ByteFormat.Human(current.SizeBytes)
            : "";
        IsCurrentVideo = current?.Kind == MediaKind.Video;
        CurrentDurationText = current?.DurationText ?? "";
        CanUndo = _history.Count > 0;
    }

    public Task KeepAsync() => DecideAsync(SwipeDecision.Keep, album: null);

    public Task TrashAsync() => DecideAsync(SwipeDecision.Trash, album: null);

    /// <summary>"Not sure yet" — pushes the photo to the end of the queue without a decision.</summary>
    public void Later()
    {
        if (_queue.Count <= 1)
            return;
        var photo = _queue[0];
        _queue.RemoveAt(0);
        _queue.Add(photo);
        RefreshCards();
    }

    /// <summary>Returns true when a photo was actually moved (so the view can animate the card away).</summary>
    public async Task<bool> MoveToAlbumAsync()
    {
        var photo = CurrentPhoto;
        if (photo is null)
            return false;

        var album = await albumPicker.PickAsync();
        if (album is null)
            return false;

        var idAfterMove = await gallery.Library.MoveToAlbumAsync(photo.Id, album);
        if (idAfterMove is null)
        {
            await Shell.Current.DisplayAlertAsync("Move failed",
                "This photo could not be moved. It may be read-only or already gone.", "OK");
            return false;
        }

        await DecideAsync(SwipeDecision.Moved, album);

        // Copy-style moves (WhatsApp sources, browsed folders) give the file a new
        // identity — mark it reviewed too so it never comes back into a deck.
        if (idAfterMove != photo.Id)
        {
            await gallery.Store.UpsertAsync(new ReviewRecord
            {
                PhotoId = idAfterMove,
                MonthKey = photo.MonthKey,
                Decision = SwipeDecision.Moved,
                DecidedAt = DateTime.UtcNow,
                SizeBytes = photo.SizeBytes,
                TargetAlbum = album.Name,
            });
        }

        gallery.Invalidate(); // folder contents changed; copy-style moves even change ids
        return true;
    }

    public async Task UndoAsync()
    {
        if (_history.Count == 0)
            return;

        var (photo, _) = _history.Pop();
        await gallery.Store.RemoveAsync(photo.Id);
        _queue.Insert(0, photo);
        await RefreshBinBadgeAsync();
        RefreshCards();
    }

    private async Task DecideAsync(SwipeDecision decision, AlbumInfo? album)
    {
        var photo = CurrentPhoto;
        if (photo is null)
            return;

        // The bin shows how much space you'll free — on iOS the size isn't known up front.
        long size = photo.SizeBytes;
        if (size == 0 && decision == SwipeDecision.Trash)
            size = await gallery.Library.GetFileSizeAsync(photo.Id);

        await gallery.Store.UpsertAsync(new ReviewRecord
        {
            PhotoId = photo.Id,
            MonthKey = photo.MonthKey, // the photo's own month — smart decks span months
            Decision = decision,
            DecidedAt = DateTime.UtcNow,
            SizeBytes = size,
            TargetAlbum = album?.Name,
        });

        _history.Push((photo, decision));
        _queue.RemoveAt(0);

        if (decision == SwipeDecision.Trash)
            await RefreshBinBadgeAsync();

        RefreshCards();

        try
        {
            HapticFeedback.Default.Perform(HapticFeedbackType.Click);
        }
        catch
        {
            // haptics are best-effort
        }
    }
}
