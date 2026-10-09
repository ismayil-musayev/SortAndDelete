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
    private readonly Stack<Step> _history = new();
    private int _total;
    private bool _initialized;

    /// <summary>
    /// One decision, as Undo needs it. A copy-style move replaced the photo with a new item, the
    /// copy (<see cref="CopyId"/>, null when Android didn't say which): Undo brings the copy back,
    /// never the original's id, which is gone.
    /// </summary>
    private sealed record Step(PhotoAsset Photo, bool Copied = false, string? CopyId = null, string? AlbumName = null);

    // A move owns the deck until it ends: closing the album picker re-syncs the deck (OnAppearing),
    // and a re-dealt deck must not change which photo the move is about. That re-sync waits.
    private bool _moving;
    private bool _resyncPending;

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
    [ObservableProperty] private bool isCopying;
    [ObservableProperty] private double copyProgress;
    [ObservableProperty] private string copyText = "";

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
            if (_moving)
                _resyncPending = true;
            else
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

    public Task KeepAsync() => CurrentPhoto is { } photo ? DecideAsync(photo, SwipeDecision.Keep, album: null) : Task.CompletedTask;

    public Task TrashAsync() => CurrentPhoto is { } photo ? DecideAsync(photo, SwipeDecision.Trash, album: null) : Task.CompletedTask;

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
        if (photo is null || _moving)
            return false;

        _moving = true;
        try
        {
            return await MoveAsync(photo);
        }
        finally
        {
            _moving = false;
            IsCopying = false;
            if (_resyncPending)
            {
                _resyncPending = false;
                await ResyncAsync();
            }
        }
    }

    /// <summary>Moves <paramref name="photo"/>, the card the user pressed the button on, whatever the deck shows meanwhile.</summary>
    private async Task<bool> MoveAsync(PhotoAsset photo)
    {
        AlbumInfo? album;
        MoveResult result;
        try
        {
            album = await albumPicker.PickAsync(photo.Kind);
            if (album is null)
                return false;

            CopyText = $"Copying to {album.Name}…";
            CopyProgress = 0;
            var feedback = new MoveFeedback
            {
                // Large items only. The copy is checked before the original is touched.
                Progress = new Progress<double>(value =>
                {
                    if (!_moving)
                        return;
                    CopyProgress = value;
                    IsCopying = value < 1;
                }),
                ConfirmAsync = (title, message, accept) =>
                    Shell.Current.DisplayAlertAsync(title, message, accept, "Cancel"),
            };
            result = await gallery.Library.MoveToAlbumAsync(photo.Id, album, feedback);
        }
        catch (Exception)
        {
            // The move reports its own failures; this is the unforeseen, kept from crashing the app.
            await Shell.Current.DisplayAlertAsync("Move failed",
                "Something went wrong while moving it. Check the album before trying again.", "OK");
            return false;
        }
        finally
        {
            IsCopying = false;
        }

        if (result.Outcome == MoveOutcome.Cancelled)
            return false; // the user said no in a system dialog — nothing to explain

        if (result.Outcome != MoveOutcome.Moved)
        {
            await Shell.Current.DisplayAlertAsync("Move failed",
                result.Problem ?? "This photo could not be moved.", "OK");
            return false;
        }

        // Copy-style moves (WhatsApp sources, Download/Documents, folders reached through a
        // folder grant) give the file a new identity — mark it reviewed too so it never comes
        // back into a deck.
        bool copied = result.PhotoId != photo.Id;
        if (copied && result.PhotoId is { } copyId)
        {
            await gallery.Store.UpsertAsync(new ReviewRecord
            {
                PhotoId = copyId,
                MonthKey = photo.MonthKey,
                Decision = SwipeDecision.Moved,
                DecidedAt = DateTime.UtcNow,
                SizeBytes = photo.SizeBytes,
                TargetAlbum = album.Name,
            });
        }

        gallery.Invalidate(); // folder contents changed; copy-style moves even change ids

        if (result.OriginalKept)
        {
            // Android kept the original too: it stays in the deck, to delete or keep.
            await Shell.Current.DisplayAlertAsync("Now in both places", result.Note ?? "", "OK");
            return false;
        }

        await DecideAsync(photo, SwipeDecision.Moved, album,
            new Step(photo, copied, copied ? result.PhotoId : null, album.Name));

        if (result.Note is { } note)
            await Shell.Current.DisplayAlertAsync("Moved", note, "OK");
        return true;
    }

    public async Task UndoAsync()
    {
        if (_history.Count == 0)
            return;

        var step = _history.Pop();
        var photo = step.Photo;
        if (step.Copied)
        {
            // The original is gone (to the system trash, or deleted); its copy is the photo now.
            var copy = step.CopyId is { } copyId ? await FindAsync(copyId) : null;
            if (copy is null)
            {
                CanUndo = _history.Count > 0;
                await Shell.Current.DisplayAlertAsync("Can't undo the move",
                    $"It's in {step.AlbumName ?? "the album"} now as a copy, and Sort & Delete can't " +
                    "see that copy to bring it back here.", "OK");
                return;
            }

            await gallery.Store.RemoveAsync(photo.Id);
            await gallery.Store.RemoveAsync(copy.Id);
            photo = copy;
        }
        else
        {
            await gallery.Store.RemoveAsync(photo.Id);
        }

        _queue.RemoveAll(p => p.Id == photo.Id);
        _queue.Insert(0, photo);
        await RefreshBinBadgeAsync();
        RefreshCards();
    }

    /// <summary>The library's item with this id, or null when the app can't see it.</summary>
    private async Task<PhotoAsset?> FindAsync(string photoId)
    {
        await gallery.GetPhotosAsync(); // reloads after a move invalidated it
        return gallery.Find(photoId);
    }

    /// <summary>
    /// Records the decision about <paramref name="photo"/> and takes it off the deck, by id: the
    /// deck can have been re-dealt since the user acted on it.
    /// </summary>
    private async Task DecideAsync(PhotoAsset photo, SwipeDecision decision, AlbumInfo? album, Step? step = null)
    {
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

        _history.Push(step ?? new Step(photo));
        _queue.RemoveAll(p => p.Id == photo.Id);

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
