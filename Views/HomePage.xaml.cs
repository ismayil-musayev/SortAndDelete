using SortAndDelete.ViewModels;

namespace SortAndDelete.Views;

public partial class HomePage : ContentPage
{
    private readonly HomeViewModel _viewModel;

    public HomePage(HomeViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // Reload every time we come back — progress/bin counts change on other pages.
        await _viewModel.LoadAsync();
    }
}
