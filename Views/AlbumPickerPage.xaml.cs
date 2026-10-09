using SortAndDelete.Logic;
using SortAndDelete.Models;
using SortAndDelete.Services;

namespace SortAndDelete.Views;

public partial class AlbumPickerPage : ContentPage
{
    private readonly IPhotoLibraryService _library;
    private readonly MediaKind _kind;
    private readonly TaskCompletionSource<AlbumInfo?> _result = new();

    // One choice at a time: a second tap would start a second dialog and a second close.
    private bool _busy;

    /// <summary>Completes with the chosen album, or null when the picker is dismissed.</summary>
    public Task<AlbumInfo?> Result => _result.Task;

    public AlbumPickerPage(IReadOnlyList<MoveTarget> targets, MediaKind kind, IPhotoLibraryService library)
    {
        InitializeComponent();
        _library = library;
        _kind = kind;
        AlbumList.ItemsSource = targets;
        BrowseButton.IsVisible = library.SupportsFolderBrowsing;
    }

    /// <summary>System folder browser — pick any folder on the device (Android).</summary>
    private async void OnBrowseClicked(object? sender, EventArgs e) => await ChooseAsync(async () =>
    {
        var pick = await _library.PickExternalFolderAsync(_kind);
        if (pick.Problem is { } problem)
        {
            // Nothing moved yet: the picker stays open for another choice.
            await DisplayAlertAsync("Can't move there", problem, "OK");
            return;
        }

        if (pick.Album is { } album)
            await CloseAsync(album);
    });

    private async void OnAlbumTapped(object? sender, TappedEventArgs e) => await ChooseAsync(async () =>
    {
        if (e.Parameter is not MoveTarget target)
            return;

        if (target.NeedsFolderAccess && !await GetFolderAccessAsync(target.Album))
            return;

        await CloseAsync(target.Album);
    });

    /// <summary>
    /// Says why Android is about to show its folder picker, then has the user grant the album's
    /// folder there. Once granted, later moves into it go straight through.
    /// </summary>
    private async Task<bool> GetFolderAccessAsync(AlbumInfo album)
    {
        var items = _kind == MediaKind.Video ? "videos" : "photos";
        var list = MoveRouting.InWords(MoveRouting.FoldersWithoutAsking(_kind, OperatingSystem.IsAndroidVersionAtLeast(30)));
        var steps = OperatingSystem.IsAndroidVersionAtLeast(30)
            ? "on the next screen, tap “Use this folder”, then “Allow”"
            : "on the next screen, select this folder and allow access";

        bool proceed = await DisplayAlertAsync($"Allow access to {album.Name}",
            $"Android lets apps put {items} in {list} without asking for folder access. For {album.Name} " +
            $"it asks you once: {steps}.",
            "Continue", "Cancel");
        if (!proceed)
            return false;

        switch (await _library.RequestFolderAccessAsync(album))
        {
            case FolderAccessResult.Granted:
                return true;
            case FolderAccessResult.OtherFolder:
                await DisplayAlertAsync("That's a different folder",
                    $"Nothing was moved. To move into {album.Name}, choose {album.Name} itself on that screen.",
                    "OK");
                return false;
            default:
                return false; // backed out of the system screen
        }
    }

    private async void OnCreateClicked(object? sender, EventArgs e) => await ChooseAsync(async () =>
    {
        var name = await DisplayPromptAsync("New album", "Name for the new album:",
            "Create", "Cancel", placeholder: "e.g. Travel", maxLength: 50);
        if (string.IsNullOrWhiteSpace(name))
            return;

        var album = await _library.CreateAlbumAsync(name.Trim(), _kind);
        if (album is null)
        {
            await DisplayAlertAsync("Couldn't create album", "Try a different name.", "OK");
            return;
        }

        await CloseAsync(album);
    });

    private async void OnCancelClicked(object? sender, EventArgs e) => await CloseAsync(null);

    /// <summary>
    /// Runs one choice at a time, and turns a failure to open a system screen (no folder picker
    /// on the device, the activity gone) into a message instead of a crash.
    /// </summary>
    private async Task ChooseAsync(Func<Task> choice)
    {
        if (_busy)
            return;

        _busy = true;
        try
        {
            await choice();
        }
        catch (Exception)
        {
            try
            {
                await DisplayAlertAsync("Couldn't open that",
                    "Android couldn't show the screen for this. Nothing was moved.", "OK");
            }
            catch (Exception)
            {
                // the page is gone with its window; nothing left to tell
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task CloseAsync(AlbumInfo? album)
    {
        _result.TrySetResult(album);

        // The window can be gone by now (the activity was destroyed behind a system screen).
        if (!Navigation.ModalStack.Contains(this))
            return;
        try
        {
            await Navigation.PopModalAsync();
        }
        catch (Exception)
        {
            // already closing with its window
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Covers hardware-back / swipe-down dismissal.
        _result.TrySetResult(null);
    }
}
