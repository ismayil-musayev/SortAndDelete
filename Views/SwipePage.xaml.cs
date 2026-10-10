using SortAndDelete.Models;
using SortAndDelete.Services;
using SortAndDelete.ViewModels;

namespace SortAndDelete.Views;

public partial class SwipePage : ContentPage, IDeckCardView, IResumeAware
{
    /// <summary>
    /// How long after it appears the page counts as in front even if no navigation says so. The
    /// page arrives when the navigation to it ends (OnNavigatedTo), and nothing can be pressed on
    /// it before that; should that event ever not come, the deck would be dead. Far longer than
    /// any page takes to slide off it.
    /// </summary>
    private static readonly TimeSpan ArrivalFallback = TimeSpan.FromSeconds(1.5);

    private readonly SwipeViewModel _viewModel;
    private bool _animating;
    private bool _zoomed;

    // When the press now on each of the deck's buttons began, noted as the button goes down and
    // taken when its click arrives. Per button: two fingers can be on two of them at once.
    private readonly Dictionary<object, long> _pressBeganAt = [];

    // Whether the navigation to this page has ended (see OnNavigatedTo), and the count of its
    // appearances, for the fallback that stands in for that event.
    private bool _arrived;
    private int _appearances;

    // What is on the two cards, kept so it can be put on them once they are built (the first
    // picture can be decoded before the page is) and again should they be built anew.
    private (string PhotoId, CardPicture? Picture)? _front;
    private CardPicture? _back;

    // Counts the times the front card was shown or hidden, so the frame a card waits for reports
    // nothing once that card has gone.
    private int _shows;

    // How often in a row the front card's picture was found missing from it when its frame came
    // (see OnFrontDrawn), so that putting it back is not tried for ever.
    private int _putsAgain;

    public SwipePage(SwipeViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        _viewModel.Attach(this);

        // Loaded, not HandlerChanged: a handler is given to its control before it is set up, and
        // setting it up maps the control's (empty) source, which clears the image view again.
        CardImage.Loaded += (_, _) =>
        {
            PutFrontPicture();
            _viewModel.OnCardBuilt();
        };
        BackImage.Loaded += (_, _) => PutPicture(BackImage, _back);

        // Taken apart (popped, or its window destroyed): the decoded pictures are let go of now
        // rather than whenever they happen to be collected.
        HandlerChanged += (_, _) =>
        {
            if (Handler is null)
                _viewModel.ReleasePictures();
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // Should the navigation's end never be reported, the page arrives all the same, well
        // after anything can still be sliding off it, if it is still the page in front then.
        int appearance = ++_appearances;
        Dispatcher.DispatchDelayed(ArrivalFallback, () =>
        {
            if (appearance == _appearances && !_arrived && Shell.Current?.CurrentPage == this)
                Arrive();
        });

        await _viewModel.InitAsync();
    }

    // The page is in front once the navigation to it has ended, which is after the page that was
    // over it has slid away: while that one slides, it still covers the card, and Android already
    // lets presses through to the buttons underneath. Appearing comes too early for this: it is
    // raised as the navigation starts.
    protected override void OnNavigatedTo(NavigatedToEventArgs args)
    {
        base.OnNavigatedTo(args);
        Arrive();
    }

    protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
    {
        base.OnNavigatedFrom(args);
        _arrived = false;
        _viewModel.OnPageLeft();

        // Gone for good (Back), not merely under the bin or the album picker.
        if (Shell.Current?.Navigation?.NavigationStack.Contains(this) != true)
            _viewModel.ReleasePictures();
    }

    private void Arrive()
    {
        _arrived = true;
        _viewModel.OnPageArrived(PressClock.Now);
    }

    /// <summary>
    /// The app has come back with the deck up: the card is looked at from now. It was drawn
    /// before the app went away, but nobody has seen it since.
    /// </summary>
    public Task OnAppResumedAsync()
    {
        if (_arrived)
            _viewModel.OnPageArrived(PressClock.Now);
        return Task.CompletedTask;
    }

    // ---------- The card (IDeckCardView) ----------

    /// <summary>
    /// Puts the picture on the front card and brings the card up, at rest. The view model calls
    /// this in the step it sets the card's label and counter in, and on Android the picture is a
    /// decoded bitmap handed straight to the image view, so the next frame shows all of them
    /// together: the label never names a photo over the picture of another.
    /// </summary>
    public void ShowCard(string photoId, CardPicture? picture)
    {
        _front = (photoId, picture);
        _putsAgain = 0;
        ResetCard();
        PutFrontPicture();
    }

    public void HideCard()
    {
        _shows++;
        _front = null;
        FrontCard.Opacity = 0;
        ResetCard();
        PutPicture(CardImage, null);
    }

    public void ShowBackCard(CardPicture? picture)
    {
        _back = picture;
        PutPicture(BackImage, picture);
    }

    /// <summary>
    /// Puts the front card's picture on it and, once a frame with it has been drawn, tells the
    /// view model so (see <see cref="SwipeViewModel.OnCardDrawn"/>). Before the card is built
    /// this does nothing, and the card stays off: it runs again when the card is (Loaded). The
    /// card comes up here, in the step its picture goes on, so its label never shows without it.
    /// </summary>
    private void PutFrontPicture()
    {
        if (_front is not { } front)
            return;

        if (!PutPicture(CardImage, front.Picture))
        {
            FrontCard.Opacity = 0; // its label is set already: not shown without its picture
            return;
        }

        FrontCard.Opacity = 1;
        int show = ++_shows;
#if ANDROID
        if (CardImage.Handler?.PlatformView is Android.Views.View view)
        {
            // The first of these runs as the next frame begins, before that frame draws the
            // card; by the time the second runs, a frame later, it has been drawn.
            view.PostOnAnimation(new Java.Lang.Runnable(() =>
                view.PostOnAnimation(new Java.Lang.Runnable(() => OnFrontDrawn(show, front)))));
            return;
        }
#endif
        OnFrontDrawn(show, front);
    }

    /// <summary>
    /// A frame has been drawn since the picture was put on the front card. The view model is
    /// told only if the card still holds that very picture: should anything have taken it off
    /// the image view meanwhile, it is put back and its frame waited for again, so a card is
    /// never reported as shown with its label alone.
    /// </summary>
    private void OnFrontDrawn(int show, (string PhotoId, CardPicture? Picture) front)
    {
        if (show != _shows)
            return; // that card has gone, or been put up again since

        if (!HoldsPicture(CardImage, front.Picture))
        {
            if (_putsAgain++ < 3)
                PutFrontPicture();
            return;
        }

        _putsAgain = 0;
        _viewModel.OnCardDrawn(front.PhotoId, PressClock.Now);
    }

    /// <summary>
    /// Puts a picture on a card's image, or clears it; false when the image isn't built yet. On
    /// Android the decoded bitmap goes to the image view itself, in this call: an image control
    /// given a source would decode it in the background and swap it in frames later.
    /// </summary>
    private static bool PutPicture(Image image, CardPicture? picture)
    {
#if ANDROID
        if (!image.IsLoaded || image.Handler?.PlatformView is not Android.Widget.ImageView view)
            return false;

        // No picture is no drawable at all: handed a null bitmap, the image view would wrap it
        // in a drawable of its own, and so never look empty again.
        if (picture?.Bitmap is { } bitmap)
            view.SetImageBitmap(bitmap);
        else
            view.SetImageDrawable(null);
        return true;
#else
        image.Source = picture?.Source;
        return true;
#endif
    }

    /// <summary>
    /// Whether a card's image holds exactly this picture now (none, for a photo that has none).
    /// Checked on Android, where the picture is the image view's own bitmap; elsewhere the image
    /// control loads its source by itself, as it always has.
    /// </summary>
    private static bool HoldsPicture(Image image, CardPicture? picture)
    {
#if ANDROID
        if (image.Handler?.PlatformView is not Android.Widget.ImageView view)
            return false;

        if (picture is null)
            return view.Drawable is null or Android.Graphics.Drawables.BitmapDrawable { Bitmap: null };

        return picture.Bitmap is { } bitmap &&
               view.Drawable is Android.Graphics.Drawables.BitmapDrawable { Bitmap: { } held } &&
               Android.Runtime.JNIEnv.IsSameObject(held.Handle, bitmap.Handle);
#else
        return true;
#endif
    }

    // ---------- Presses ----------

    // A press on one of the deck's buttons begins: its click, which comes when the finger lifts,
    // is about the card that was up now. On Android the touch's own time says when that was,
    // noted by the activity before anything handled it.
    private void OnDeckButtonPressed(object? sender, EventArgs e)
    {
        if (sender is not null)
            _pressBeganAt[sender] = PressClock.LastFingerDownAt ?? PressClock.Now;
    }

    /// <summary>
    /// The photo the press on <paramref name="button"/> now arriving as a click decides: the one
    /// on the card when that press began, if it had been there to be seen (see
    /// <see cref="SwipeViewModel.PhotoDecidedBy"/>); null when the press decides nothing. A click
    /// without a press (a keyboard, an accessibility service) counts from now.
    /// </summary>
    private PhotoAsset? TakePressedPhoto(object? button)
    {
        long pressBeganAt = PressClock.Now;
        if (button is not null && _pressBeganAt.Remove(button, out long noted))
            pressBeganAt = noted;
        return _viewModel.PhotoDecidedBy(pressBeganAt);
    }

    /// <summary>Double-tap zooms into the photo; swiping is disabled until zoomed back out.</summary>
    private async void OnCardDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_animating || _viewModel.ShownPhoto is null)
            return;

        _zoomed = !_zoomed;
        if (_zoomed)
        {
            var point = e.GetPosition(CardImage);
            if (point is Point p && CardImage.Width > 0 && CardImage.Height > 0)
            {
                // Zoom towards the tapped spot.
                CardImage.AnchorX = Math.Clamp(p.X / CardImage.Width, 0, 1);
                CardImage.AnchorY = Math.Clamp(p.Y / CardImage.Height, 0, 1);
            }
            await CardImage.ScaleToAsync(2.5, 180, Easing.CubicOut);
        }
        else
        {
            await CardImage.ScaleToAsync(1, 180, Easing.CubicOut);
            CardImage.AnchorX = 0.5;
            CardImage.AnchorY = 0.5;
        }
    }

    private async Task SwipeOutAsync(object? button, bool keep)
    {
        // The card the press was made on, taken now: the deck can be re-dealt while it flies out.
        var photo = TakePressedPhoto(button);
        if (_animating || photo is null)
            return;

        _animating = true;
        try
        {
            (keep ? KeepBadge : DeleteBadge).Opacity = 1;
            double width = Math.Max(DeckArea.Width, 320);
            double targetX = keep ? width + 160 : -(width + 160);

            await Task.WhenAll(
                FrontCard.TranslateToAsync(targetX, FrontCard.TranslationY + 40, 200, Easing.CubicIn),
                FrontCard.RotateToAsync(keep ? 18 : -18, 200, Easing.CubicIn));

            // Taking the photo off the deck takes its card off, and the next photo's comes up
            // with its picture (see SwipeViewModel.RefreshCards).
            if (keep)
                await _viewModel.KeepAsync(photo);
            else
                await _viewModel.TrashAsync(photo);
        }
        finally
        {
            // Still this photo's card: it wasn't decided after all, so it comes back into view.
            if (_viewModel.ShownPhoto?.Id == photo.Id)
                ResetCard();
            _animating = false;
        }
    }

    /// <summary>Puts the front card back at rest, whatever it was doing.</summary>
    private void ResetCard()
    {
        FrontCard.CancelAnimations();
        CardImage.CancelAnimations();
        FrontCard.TranslationX = 0;
        FrontCard.TranslationY = 0;
        FrontCard.Rotation = 0;
        KeepBadge.Opacity = 0;
        DeleteBadge.Opacity = 0;
        _zoomed = false;
        CardImage.Scale = 1;
        CardImage.AnchorX = 0.5;
        CardImage.AnchorY = 0.5;
    }

    private async void OnKeepClicked(object? sender, EventArgs e) => await SwipeOutAsync(sender, keep: true);

    private async void OnDeleteClicked(object? sender, EventArgs e) => await SwipeOutAsync(sender, keep: false);

    private void OnLaterClicked(object? sender, EventArgs e)
    {
        var photo = TakePressedPhoto(sender);
        if (!_animating && photo is not null)
            _viewModel.Later(photo);
    }

    // An undo runs alone, like a decision: a press that lands while it is being applied would
    // otherwise be judged against a deck that is about to change under it.
    private async void OnUndoClicked(object? sender, EventArgs e)
    {
        if (_animating)
            return;

        _animating = true;
        try
        {
            await _viewModel.UndoAsync();
        }
        finally
        {
            _animating = false;
        }
    }

    private async void OnAlbumClicked(object? sender, EventArgs e)
    {
        var photo = TakePressedPhoto(sender);
        if (_animating || photo is null)
            return;

        _animating = true;
        try
        {
            await _viewModel.MoveToAlbumAsync(photo);
        }
        finally
        {
            _animating = false;
        }
    }

    private async void OnOpenExternallyClicked(object? sender, EventArgs e)
    {
        if (!_animating)
            await _viewModel.OpenExternallyAsync();
    }

    private async void OnReReviewClicked(object? sender, EventArgs e)
    {
        if (!_animating)
            await _viewModel.ReReviewAsync();
    }

    private async void OnBackClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");

    private async void OnBinClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync(nameof(BinPage));
}
