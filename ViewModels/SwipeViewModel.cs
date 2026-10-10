using CommunityToolkit.Mvvm.ComponentModel;
using SortAndDelete.Logic;
using SortAndDelete.Models;
using SortAndDelete.Services;

namespace SortAndDelete.ViewModels;

/// <summary>
/// The deck page's card, as the view model drives it. The page puts a picture on the card in the
/// call that asks for it, which is the same step the view model sets the card's label in, so the
/// two reach the screen in one frame.
/// </summary>
public interface IDeckCardView
{
    /// <summary>
    /// Puts <paramref name="picture"/> (null: this photo has none to show) on the front card and
    /// brings the card up, at rest. Once a frame with it has been drawn, the view calls
    /// <see cref="SwipeViewModel.OnCardDrawn"/> for <paramref name="photoId"/>.
    /// </summary>
    void ShowCard(string photoId, CardPicture? picture);

    /// <summary>Takes the front card off the screen.</summary>
    void HideCard();

    /// <summary>Puts the picture of the photo behind the front card on the back card; null clears it.</summary>
    void ShowBackCard(CardPicture? picture);
}

[QueryProperty(nameof(MonthKey), "month")]
[QueryProperty(nameof(ModeName), "mode")]
[QueryProperty(nameof(FolderId), "folder")]
public partial class SwipeViewModel(GalleryService gallery, IAlbumPickerService albumPicker) : ObservableObject
{
    private const int CardPixelSize = 2048;

    /// <summary>
    /// How long a card must have been up before a press can be about it (see <see cref="ShownCard"/>):
    /// well under the time anyone takes to react to a picture, let alone judge one, so it only
    /// ever refuses a press that was on its way before the card appeared.
    /// </summary>
    private const long MinimumLookMs = 150;

    private List<PhotoAsset> _queue = [];
    private readonly Stack<Step> _history = new();
    private int _total;
    private bool _initialized;
    private bool _initializing;

    // The card. The deck's front is the photo that belongs on it; the photo that is on it, picture
    // and label, is _shown: the front, or none while the front's picture is being decoded. What a
    // press decides goes by the card alone (see ShownCard).
    private readonly ShownCard _card = new(MinimumLookMs);
    private PhotoAsset? _shown;
    private IDeckCardView? _view;

    // Counts the deals of the deck, so a picture that took its time finds out whether the deck it
    // was decoded for is still the one on the table.
    private int _deal;

    // The decoded pictures of the cards about to be shown (see DeckCard.PicturesToKeep), by photo
    // id, each with the photo as it was when its picture was decoded.
    private readonly Dictionary<string, (PhotoAsset Of, Task<CardPicture?> Decoding)> _pictures = new(StringComparer.Ordinal);

    // The photo whose picture is on the back card.
    private string? _behind;

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
    [ObservableProperty] private string positionText = "";
    [ObservableProperty] private string currentDateText = "";
    [ObservableProperty] private string currentLocationText = "";
    [ObservableProperty] private string currentSizeText = "";
    [ObservableProperty] private bool isMonthMode;
    [ObservableProperty] private bool isCurrentVideo;
    [ObservableProperty] private string currentDurationText = "";
    [ObservableProperty] private bool isPictureMissing;
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

    /// <summary>The photo that belongs on the card: the front of the deck.</summary>
    private PhotoAsset? Front => _queue.Count > 0 ? _queue[0] : null;

    /// <summary>
    /// The photo on the card, picture and label together; null while the card is off, as it is
    /// while the next photo's picture is being decoded. See <see cref="PhotoDecidedBy"/>.
    /// </summary>
    public PhotoAsset? ShownPhoto => _shown;

    /// <summary>Whether the ↗ open-in-gallery button is shown (Android only).</summary>
    public bool CanOpenExternally => gallery.Library.SupportsOpenExternally;

    /// <summary>The page's card, which this drives. Set once, by the page, before it loads the deck.</summary>
    public void Attach(IDeckCardView view) => _view = view;

    /// <summary>
    /// The photo a press on one of the deck's buttons decides, by when the press began: the one on
    /// the card, if its picture had been on screen, with the page in front, long enough by then to
    /// have been seen (see <see cref="ShownCard"/>). Null when the press decides nothing: no card
    /// is up, its picture hasn't been drawn, another page was still sliding off it, or the finger
    /// was already on its way when the card appeared.
    /// </summary>
    public PhotoAsset? PhotoDecidedBy(long pressBeganAt) =>
        _shown is { } shown && _card.DecidedBy(pressBeganAt) == shown.Id && _queue.Any(p => p.Id == shown.Id)
            ? shown
            : null;

    /// <summary>
    /// The page's card has been built, for the first time or anew: the deck's front is put on it
    /// if it isn't there, as after its pictures were let go of (see <see cref="ReleasePictures"/>).
    /// </summary>
    public void OnCardBuilt()
    {
        if (_initialized)
            RefreshCards();
    }

    /// <summary>A frame showing <paramref name="photoId"/>'s card has been drawn (from the page).</summary>
    public void OnCardDrawn(string photoId, long at) => _card.Drawn(photoId, at);

    /// <summary>The deck page is in front, and whatever slid off it has gone (from the page).</summary>
    public void OnPageArrived(long at) => _card.PageArrived(at);

    /// <summary>Another page is in front of the deck (from the page).</summary>
    public void OnPageLeft() => _card.PageLeft();

    /// <summary>Hands the item on the card to the default viewer app (or the system chooser).</summary>
    public async Task OpenExternallyAsync()
    {
        var photo = _shown;
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

        _initializing = true;
        UpdateLoading();
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
            _initializing = false;
            UpdateLoading();
        }
    }

    private string? Folder => string.IsNullOrEmpty(FolderId) ? null : FolderId;

    /// <summary>Fresh queue load — used by init and by "Re-review month".</summary>
    private async Task LoadQueueAsync()
    {
        int? monthTotal = await MonthTotalAsync();
        await RefreshBinBadgeAsync();
        var queue = await gallery.GetQueueAsync(Mode, MonthKey, Folder);

        _queue = queue;
        _total = monthTotal ?? queue.Count;
        _history.Clear();
        RefreshCards();
    }

    /// <summary>
    /// Re-entry after visiting the bin or album picker: drop photos reviewed elsewhere, put
    /// restored photos next in line, keep the rest of the queue order intact.
    /// </summary>
    private async Task ResyncAsync()
    {
        // The Biggest deck keeps the total fixed from init; the position is clamped if restores
        // grow the queue. Restored photos go behind the card that is up, not in its place: the
        // user comes back to the card they left, and a tap on it must not find another photo
        // there (see DeckCard.Redeal). With no card up they go to the front.
        int? monthTotal = await MonthTotalAsync();
        await RefreshBinBadgeAsync();
        var fresh = await gallery.GetQueueAsync(Mode, MonthKey, Folder);

        _queue = DeckCard.Redeal(_queue, fresh, photo => photo.Id, _shown?.Id);
        _total = monthTotal ?? _total;
        RefreshCards();
    }

    /// <summary>How many items the deck's month has, reviewed or not; null for a deck that isn't a month's.</summary>
    private async Task<int?> MonthTotalAsync() =>
        Mode == DeckMode.Month
            ? (await gallery.GetPhotosAsync(Folder)).Count(p => p.MonthKey == MonthKey)
            : null;

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

    // ---------- The card ----------

    /// <summary>
    /// Brings the card in line with the deck after the deck changed. The photo at the front
    /// belongs on the card, and gets there only together with its picture: the label, the counter
    /// and the picture change in one step (<see cref="ShowOnCard"/>), once the picture is decoded.
    /// Until then no card is up: one that shows another photo than the front goes at once (see
    /// <see cref="DeckCard.After"/>), so there is never a card to press that isn't the front's.
    /// </summary>
    private void RefreshCards()
    {
        int deal = ++_deal;
        CanUndo = _history.Count > 0;
        IsDone = _queue.Count == 0;

        var change = DeckCard.After(_shown?.Id, [.. _queue.Take(1).Select(p => p.Id)]);
        if (change is CardChange.Replace or CardChange.Done)
            TakeCardOff();

        KeepPictures();

        if (change == CardChange.Done)
        {
            PositionText = "";
            Progress = 1;
        }
        else if (change == CardChange.Stay)
        {
            // Still this photo's card: only its place in the deck, and the one behind it, can have changed.
            UpdatePosition();
            ShowBehind();
        }
        else if (Front is { } front)
        {
            _ = PutOnCardWhenReadyAsync(front, deal);
        }

        UpdateLoading();
    }

    /// <summary>
    /// Decodes <paramref name="front"/>'s picture, off the UI thread, and then puts the photo on
    /// the card, unless the deck was dealt again meanwhile: that deal sees to its own front.
    /// </summary>
    private async Task PutOnCardWhenReadyAsync(PhotoAsset front, int deal)
    {
        CardPicture? picture = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                picture = await PictureOf(front);
            }
            catch (Exception)
            {
                picture = null; // it can't be decoded: the card says so, and can still be decided
            }

            if (deal != _deal)
                return;
            if (picture is not { IsLetGo: true })
                break;

            // Let go of while this waited for it (the page was leaving): decoded once more, as a
            // picture that isn't there must never go up under a label as if it were.
            _pictures.Remove(front.Id);
            picture = null;
        }

        ShowOnCard(front, picture);
    }

    /// <summary>
    /// Puts a photo on the card: its label, its place in the deck and its picture, in this one
    /// step, with nothing awaited between them, so that one frame shows them all. From here the
    /// card is this photo's, and a press decides it once that frame is up (see <see cref="ShownCard"/>).
    /// </summary>
    private void ShowOnCard(PhotoAsset photo, CardPicture? picture)
    {
        _shown = photo;
        _card.Put(photo.Id);

        CurrentDateText = photo.TakenAt.ToString("d MMM yyyy · HH:mm");
        CurrentLocationText = photo.LocationText;
        CurrentSizeText = photo.SizeBytes > 0 ? Helpers.ByteFormat.Human(photo.SizeBytes) : "";
        IsCurrentVideo = photo.Kind == MediaKind.Video;
        CurrentDurationText = photo.DurationText;
        IsPictureMissing = picture is null;
        UpdatePosition();

        _view?.ShowCard(photo.Id, picture);
        ShowBehind();
        UpdateLoading();
    }

    /// <summary>Takes the card off: nothing is on it to decide until the next photo is put there.</summary>
    private void TakeCardOff()
    {
        if (_shown is null)
            return;

        _shown = null;
        _card.TakeOff();
        _view?.HideCard();

        // Its label goes with it: the card is only out of sight, and a screen reader would
        // otherwise still read out the photo that has left.
        CurrentDateText = "";
        CurrentLocationText = "";
        CurrentSizeText = "";
        IsCurrentVideo = false;
        CurrentDurationText = "";
        IsPictureMissing = false;
    }

    /// <summary>The counter and the progress bar, for the card that is up.</summary>
    private void UpdatePosition()
    {
        int done = Math.Clamp(_total - _queue.Count, 0, Math.Max(0, _total - 1));
        PositionText = $"{done + 1} / {Math.Max(_total, 1)}";
        Progress = _total == 0 ? 1 : (double)Math.Max(0, _total - _queue.Count) / _total;
    }

    /// <summary>The spinner: while the deck is being read, and while a card's picture is being decoded with no card up.</summary>
    private void UpdateLoading() => IsLoading = _initializing || (_shown is null && _queue.Count > 0);

    /// <summary>
    /// Puts the picture of the photo behind the front on the back card, when it is decoded: a
    /// preview only, with no label and nothing to press, and never the photo that is on the front
    /// card or should be.
    /// </summary>
    private void ShowBehind()
    {
        var behind = _shown is not null && _queue.Count > 1 && _queue[0].Id == _shown.Id ? _queue[1] : null;
        if (behind?.Id == _behind)
            return;

        _behind = behind?.Id;
        _view?.ShowBackCard(null);
        if (behind is not null)
            _ = ShowBehindWhenReadyAsync(behind);
    }

    private async Task ShowBehindWhenReadyAsync(PhotoAsset behind)
    {
        CardPicture? picture;
        try
        {
            picture = await PictureOf(behind);
        }
        catch (Exception)
        {
            return;
        }

        if (_behind == behind.Id && picture is { IsLetGo: false })
            _view?.ShowBackCard(picture);
    }

    /// <summary>
    /// The decoded picture of a photo, started on first use and shared from then on. A picture
    /// decoded for the same id when its file was another (the library was read again since, and
    /// the item was edited in place, or its id given to a new file) is not this photo's: it is
    /// let go of, and the file as it is now is decoded.
    /// </summary>
    private Task<CardPicture?> PictureOf(PhotoAsset photo)
    {
        if (_pictures.TryGetValue(photo.Id, out var kept))
        {
            if (IsSameFile(kept.Of, photo))
                return kept.Decoding;
            LetGoWhenDecoded(kept.Decoding);
        }

        var decoding = gallery.LoadCardPictureAsync(photo.Id, CardPixelSize);
        _pictures[photo.Id] = (photo, decoding);
        return decoding;
    }

    private static bool IsSameFile(PhotoAsset a, PhotoAsset b) =>
        ReferenceEquals(a, b) ||
        (a.SizeBytes == b.SizeBytes && a.FileModifiedAt == b.FileModifiedAt && a.TakenAt == b.TakenAt);

    /// <summary>
    /// Starts decoding the pictures the deck is about to need and lets go of the rest (see
    /// <see cref="DeckCard.PicturesToKeep"/>). The one behind the front is decoded while the user
    /// looks at the front, so the next card comes up as soon as the last has gone.
    /// </summary>
    private void KeepPictures()
    {
        var lastDecided = _history.TryPeek(out var last) ? last.Photo : null;
        var keep = DeckCard.PicturesToKeep(_shown?.Id, [.. _queue.Take(2).Select(p => p.Id)], lastDecided?.Id);

        foreach (var photoId in _pictures.Keys.Except(keep).ToList())
        {
            _pictures.Remove(photoId, out var dropped);
            LetGoWhenDecoded(dropped.Decoding);
        }

        // The deck's own photos first, and only they replace a picture already kept for their id:
        // they are the library's as last read.
        int fromDeck = Math.Min(2, _queue.Count);
        PhotoAsset?[] wanted = [.. _queue.Take(fromDeck), _shown, lastDecided];
        for (int i = 0; i < wanted.Length; i++)
        {
            if (wanted[i] is { } photo && keep.Contains(photo.Id) && (i < fromDeck || !_pictures.ContainsKey(photo.Id)))
                _ = PictureOf(photo);
        }
    }

    /// <summary>
    /// Lets go of every decoded picture, as the page goes: they are large, and nothing else frees
    /// them before the next deck decodes its own. The card goes with them, and whatever was on its
    /// way to it is called off, so the deck is left with no card rather than one whose picture is
    /// gone; should the page show again, the front is decoded and put on the card anew.
    /// </summary>
    public void ReleasePictures()
    {
        _deal++;
        TakeCardOff();
        _behind = null;
        _view?.ShowBackCard(null);

        foreach (var kept in _pictures.Values)
            LetGoWhenDecoded(kept.Decoding);
        _pictures.Clear();
        UpdateLoading();
    }

    /// <summary>
    /// Lets go of a picture once it is decoded. A card still showing its bitmap keeps it (see
    /// <see cref="CardPicture"/>). Awaited, so that this happens on the UI thread, where pictures
    /// are put on cards: never under a card that is just taking it.
    /// </summary>
    private static async void LetGoWhenDecoded(Task<CardPicture?> decoding)
    {
        try
        {
            (await decoding)?.Dispose();
        }
        catch (Exception)
        {
            // it never decoded: nothing to let go of
        }
    }

    // ---------- Decisions ----------

    /// <summary>Keeps <paramref name="photo"/>, the card the user acted on (see <see cref="DecideOnDeckAsync"/>).</summary>
    public Task KeepAsync(PhotoAsset photo) => DecideOnDeckAsync(photo, SwipeDecision.Keep);

    /// <summary>Sends <paramref name="photo"/>, the card the user acted on, to the bin (see <see cref="DecideOnDeckAsync"/>).</summary>
    public Task TrashAsync(PhotoAsset photo) => DecideOnDeckAsync(photo, SwipeDecision.Trash);

    /// <summary>
    /// Decides about the card the user acted on, which the page takes from <see cref="PhotoDecidedBy"/>
    /// as the press arrives and hands over once the card has flown out, rather than about
    /// whatever is at the front of the deck by then: coming back from the bin re-deals the deck
    /// and puts restored photos in front. A photo the re-deal took off the deck is left alone.
    /// </summary>
    private Task DecideOnDeckAsync(PhotoAsset photo, SwipeDecision decision) =>
        _queue.Any(p => p.Id == photo.Id) ? DecideAsync(photo, decision, album: null) : Task.CompletedTask;

    /// <summary>
    /// "Not sure yet" — pushes <paramref name="photo"/>, the card the user acted on, to the end of
    /// the queue without a decision. Its card goes at once, as after a decision.
    /// </summary>
    public void Later(PhotoAsset photo)
    {
        if (_queue.Count <= 1 || _queue.RemoveAll(p => p.Id == photo.Id) == 0)
            return;

        _queue.Add(photo);
        if (_shown?.Id == photo.Id)
            TakeCardOff();
        RefreshCards();
    }

    /// <summary>
    /// Moves <paramref name="photo"/>, the card the user pressed the button on, into an album they
    /// pick. Returns true when it was moved.
    /// </summary>
    public async Task<bool> MoveToAlbumAsync(PhotoAsset photo)
    {
        if (_moving || !_queue.Any(p => p.Id == photo.Id))
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
            try
            {
                result = await gallery.Library.MoveToAlbumAsync(photo.Id, album, feedback);
            }
            finally
            {
                // Folder contents changed; copy-style moves even change ids. A move that failed or
                // was cancelled can have changed the library too: a copy it could not remove stays.
                gallery.Invalidate();
            }
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

        // What it undoes may have been carried out since: the bin emptied with this photo in it.
        // Then the photo is in the system trash, or deleted, and no longer in the bin to take back
        // out: forgetting the decision here would drop its entry under IN SYSTEM TRASH, the one
        // place it can still be restored from, and deal a card for a photo that isn't there.
        if (!step.Copied && await gallery.Store.FindAsync(photo.Id) is { Decision: SwipeDecision.Trash, CommittedAt: not null })
        {
            CanUndo = _history.Count > 0;
            await Shell.Current.DisplayAlertAsync("Can't undo that",
                gallery.Library.CanRestoreFromSystemTrash
                    ? "The bin has been emptied since, so this photo is in the system trash now. " +
                      "You can still restore it from the bin, under IN SYSTEM TRASH."
                    : "The bin has been emptied since, so this photo is no longer in it.", "OK");
            return;
        }

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

            // Undecided again. But only a photo the library has now is dealt: one deleted or moved
            // by another app meanwhile, or on a card that has been taken out, has no card to come
            // back to here.
            if (await FindAsync(photo.Id) is null)
            {
                await RefreshBinBadgeAsync();
                CanUndo = _history.Count > 0;
                await Shell.Current.DisplayAlertAsync("Undone, but it can't be shown",
                    "The decision was undone. The photo itself isn't in your gallery right now (removed by " +
                    "another app, or on storage that isn't connected), so there is no card to show for it.", "OK");
                return;
            }
        }

        // The badge first, so nothing is awaited between the deck and the card. The photo goes to
        // the front of the deck, and the card that is up goes at once (see RefreshCards): Undo is
        // the user's own doing, and the photo it brings back comes up with its picture, kept
        // decoded since the decision, so as a rule in this same step.
        await RefreshBinBadgeAsync();
        _queue.RemoveAll(p => p.Id == photo.Id);
        _queue.Insert(0, photo);
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
    /// deck can have been re-dealt since the user acted on it. Its card goes with it; the next
    /// photo's comes up when its picture is ready.
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

            // Where it is, for emptying the bin: a photo on an SD card that has been taken out by
            // then is not in the library, and must not be taken for deleted.
            VolumeName = decision == SwipeDecision.Trash && photo.VolumeName.Length > 0 ? photo.VolumeName : null,
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
