using SortAndDelete.Logic;
using SortAndDelete.ViewModels;

namespace SortAndDelete.Views;

public partial class DuplicatesPage : ContentPage
{
    private readonly DuplicatesViewModel _viewModel;

    // One per visit, so leaving stops only what this visit started, in whatever order an old
    // page's OnDisappearing and a new one's OnAppearing arrive.
    private readonly VisitScope _visit = new();

    // The window this page is listening to for the app coming back, while it is up.
    private Window? _resuming;

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
        ListenForResume();
        await _viewModel.LoadAsync(visit);
    }

    // Nobody is waiting on a scan once the page has gone. It keeps what it has read, and the
    // next visit picks up from there.
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopListeningForResume();
        _visit.Leave();
    }

    // OnAppearing doesn't run when the app comes back with this page still up, though the library
    // and the bin may both have changed meanwhile.
    private void ListenForResume()
    {
        var window = Window;
        if (ReferenceEquals(window, _resuming))
            return;

        StopListeningForResume();
        if (window is null)
            return;

        window.Resumed += OnWindowResumed;
        _resuming = window;
    }

    private void StopListeningForResume()
    {
        if (_resuming is not null)
            _resuming.Resumed -= OnWindowResumed;
        _resuming = null;
    }

    // The same check a visit starts with, under the visit the page is on. Nothing may throw out
    // of here; LoadAsync never does.
    private async void OnWindowResumed(object? sender, EventArgs e)
    {
        if (_visit.IsLive)
            await _viewModel.LoadAsync(_visit.Current);
    }

    private async void OnBackClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");
}
