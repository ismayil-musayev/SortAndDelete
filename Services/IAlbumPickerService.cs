using SortAndDelete.Models;
using SortAndDelete.Views;

namespace SortAndDelete.Services;

/// <summary>Shows the album picker and returns the chosen album (or null when cancelled).</summary>
public interface IAlbumPickerService
{
    Task<AlbumInfo?> PickAsync();
}

public sealed class AlbumPickerService(IPhotoLibraryService library) : IAlbumPickerService
{
    public async Task<AlbumInfo?> PickAsync()
    {
        var albums = await library.GetAlbumsAsync();
        var page = new AlbumPickerPage(albums, library);
        await Shell.Current.Navigation.PushModalAsync(page, animated: true);
        return await page.Result;
    }
}
