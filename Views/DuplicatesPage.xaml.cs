using SortAndDelete.Logic;
using SortAndDelete.ViewModels;

namespace SortAndDelete.Views;

public partial class DuplicatesPage : ContentPage, IResumeAware
{
    private readonly DuplicatesViewModel _viewModel;

    // One per visit, so leaving stops only what this visit started, in whatever order an old
    // page's OnDisappearing and a new one's OnAppearing arrive.
    private readonly VisitScope _visit = new();

    public DuplicatesPage(DuplicatesViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // A repeat call while the page is still up rejoins its visit: replaced, the first
        // visit's scan would be left with nothing to stop it.
        var visit = _visit.Enter();
        await _viewModel.LoadAsync(visit);
    }

    // Nobody is waiting on a scan once the page has gone. It keeps what it has read, and the
    // next visit picks up from there.
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _visit.Leave();
    }

    /// <summary>
    /// OnAppearing doesn't run when the app comes back with this page still up, though the library
    /// and the bin may both have changed meanwhile: the same check a visit starts with, under the
    /// visit the page is on. The app calls this (see <see cref="IResumeAware"/>); the page can't
    /// listen to its window for it, as it has none yet when it appears. LoadAsync never throws.
    /// </summary>
    public Task OnAppResumedAsync() =>
        _visit.IsLive ? _viewModel.LoadAsync(_visit.Current) : Task.CompletedTask;

    private async void OnBackClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");
}
