using SortAndDelete.Models;
using SortAndDelete.Services;

namespace SortAndDelete.Views;

public partial class AlbumPickerPage : ContentPage
{
    private readonly IPhotoLibraryService _library;
    private readonly TaskCompletionSource<AlbumInfo?> _result = new();

    /// <summary>Completes with the chosen album, or null when the picker is dismissed.</summary>
    public Task<AlbumInfo?> Result => _result.Task;

    public AlbumPickerPage(IReadOnlyList<AlbumInfo> albums, IPhotoLibraryService library)
    {
        InitializeComponent();
        _library = library;
        AlbumList.ItemsSource = albums;
        BrowseButton.IsVisible = library.SupportsFolderBrowsing;
    }

    /// <summary>System folder browser — pick any folder on the device (Android).</summary>
    private async void OnBrowseClicked(object? sender, EventArgs e)
    {
        var album = await _library.PickExternalFolderAsync();
        if (album is not null)
            await CloseAsync(album);
    }

    private async void OnAlbumTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is AlbumInfo album)
            await CloseAsync(album);
    }

    private async void OnCreateClicked(object? sender, EventArgs e)
    {
        var name = await DisplayPromptAsync("New album", "Name for the new album:",
            "Create", "Cancel", placeholder: "e.g. Travel", maxLength: 50);
        if (string.IsNullOrWhiteSpace(name))
            return;

        var album = await _library.CreateAlbumAsync(name.Trim());
        if (album is null)
        {
            await DisplayAlertAsync("Couldn't create album", "Try a different name.", "OK");
            return;
        }

        await CloseAsync(album);
    }

    private async void OnCancelClicked(object? sender, EventArgs e) => await CloseAsync(null);

    private async Task CloseAsync(AlbumInfo? album)
    {
        _result.TrySetResult(album);
        await Navigation.PopModalAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Covers hardware-back / swipe-down dismissal.
        _result.TrySetResult(null);
    }
}
