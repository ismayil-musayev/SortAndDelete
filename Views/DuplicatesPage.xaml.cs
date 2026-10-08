using SortAndDelete.ViewModels;

namespace SortAndDelete.Views;

public partial class DuplicatesPage : ContentPage
{
    private readonly DuplicatesViewModel _viewModel;

    public DuplicatesPage(DuplicatesViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadAsync();
    }

    private async void OnBackClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");
}
