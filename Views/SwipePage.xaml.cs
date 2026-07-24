using SortAndDelete.ViewModels;

namespace SortAndDelete.Views;

public partial class SwipePage : ContentPage
{
    private readonly SwipeViewModel _viewModel;
    private bool _animating;
    private bool _zoomed;

    public SwipePage(SwipeViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.InitAsync();
    }

    /// <summary>Double-tap zooms into the photo; swiping is disabled until zoomed back out.</summary>
    private async void OnCardDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_animating || _viewModel.CurrentPhoto is null)
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

    private async Task SwipeOutAsync(bool keep)
    {
        if (_animating || _viewModel.CurrentPhoto is null)
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

            if (keep)
                await _viewModel.KeepAsync();
            else
                await _viewModel.TrashAsync();

            ResetCard();
        }
        finally
        {
            _animating = false;
        }
    }

    private void ResetCard()
    {
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

    private async void OnKeepClicked(object? sender, EventArgs e) => await SwipeOutAsync(keep: true);

    private async void OnDeleteClicked(object? sender, EventArgs e) => await SwipeOutAsync(keep: false);

    private void OnLaterClicked(object? sender, EventArgs e)
    {
        if (!_animating)
            _viewModel.Later();
    }

    private async void OnUndoClicked(object? sender, EventArgs e)
    {
        if (!_animating)
            await _viewModel.UndoAsync();
    }

    private async void OnAlbumClicked(object? sender, EventArgs e)
    {
        if (_animating)
            return;

        _animating = true;
        try
        {
            await _viewModel.MoveToAlbumAsync();
            ResetCard();
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
