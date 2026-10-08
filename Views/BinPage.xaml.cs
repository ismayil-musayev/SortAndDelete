using SortAndDelete.ViewModels;

namespace SortAndDelete.Views;

public partial class BinPage : ContentPage
{
    private readonly BinViewModel _viewModel;

    public BinPage(BinViewModel viewModel)
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
