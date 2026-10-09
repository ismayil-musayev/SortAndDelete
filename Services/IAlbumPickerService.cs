using SortAndDelete.Models;
using SortAndDelete.Views;

namespace SortAndDelete.Services;

/// <summary>Shows the album picker and returns the chosen album (or null when cancelled).</summary>
public interface IAlbumPickerService
{
    /// <summary>Offers only the albums an item of <paramref name="kind"/> can actually be moved into.</summary>
    Task<AlbumInfo?> PickAsync(MediaKind kind);
}

public sealed class AlbumPickerService(IPhotoLibraryService library) : IAlbumPickerService
{
    public async Task<AlbumInfo?> PickAsync(MediaKind kind)
    {
        var targets = await library.GetMoveTargetsAsync(kind);
        var page = new AlbumPickerPage(targets, kind, library);
        await Shell.Current.Navigation.PushModalAsync(page, animated: true);
        return await page.Result;
    }
}
