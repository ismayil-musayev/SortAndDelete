using SortAndDelete.ViewModels;

namespace SortAndDelete.Views;

public partial class DuplicatesPage : ContentPage
{
    private readonly DuplicatesViewModel _viewModel;

    // One per visit, so leaving stops only what this visit started, in whatever order an old
    // page's OnDisappearing and a new one's OnAppearing arrive.
    private CancellationTokenSource? _visit;

    public DuplicatesPage(DuplicatesViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _visit = new CancellationTokenSource();
        await _viewModel.LoadAsync(_visit.Token);
    }

    // Nobody is waiting on a scan once the page has gone. It keeps what it has read, and the
    // next visit picks up from there. Not disposed: the scan can still be winding down on it.
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _visit?.Cancel();
    }

    private async void OnBackClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");
}
