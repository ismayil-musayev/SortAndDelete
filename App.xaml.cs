using SortAndDelete.Logic;
using SortAndDelete.Services;
using SortAndDelete.ViewModels;

namespace SortAndDelete;

public partial class App : Application
{
	private readonly GalleryService _gallery;
	private readonly ILibraryChangeWatcher _libraryWatcher;

	public App(GalleryService gallery, ILibraryChangeWatcher libraryWatcher)
	{
		InitializeComponent();
		_gallery = gallery;
		_libraryWatcher = libraryWatcher;
		_libraryWatcher.Changed += OnLibraryChanged;

		// The whole UI is designed dark — photos pop on a dark canvas.
		UserAppTheme = AppTheme.Dark;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new AppShell());

		// Other apps can change the library while this one is in the background, so the gallery
		// reads it again when next asked, and Home reloads at once if it is showing. A new window
		// counts too: Android can destroy the activity in the background and keep the app running.
		_gallery.MarkStale();
		window.Resumed += async (_, _) =>
		{
			_libraryWatcher.Start();
			await ReloadForLibraryChangeAsync([window]);
		};

		// While the window is started, from here or OnRestart (Resumed) to OnStop (Stopped), the
		// watcher reports changes as they come. A system dialog over it only pauses the activity.
		_libraryWatcher.Start();
		window.Stopped += (_, _) => _libraryWatcher.Stop();
		return window;
	}

	private async void OnLibraryChanged(object? sender, LibraryChangedEventArgs e)
	{
		// A load that began after the burst's last notification read the library with its changes
		// in it, as the loads after the app's own moves and trash requests do.
		if (ChangeDebounce.NeedsReload(e.LastNotifiedAt, _gallery.LoadStartedAt))
			await ReloadForLibraryChangeAsync([.. Windows]);
	}

	/// <summary>
	/// Marks the gallery stale, so it reads the library again when next asked, and reloads Home at
	/// once in each of <paramref name="windows"/> showing it. Not while Home shows the photo-access
	/// screen: a load asks for access, and the user said no.
	/// </summary>
	private async Task ReloadForLibraryChangeAsync(IReadOnlyList<Window> windows)
	{
		_gallery.MarkStale();
		foreach (var window in windows)
		{
			if (window.Page is not Shell { CurrentPage.BindingContext: HomeViewModel { HasAccess: true } home })
				continue;

			try
			{
				await home.ReloadAsync();
			}
			catch (Exception)
			{
				// This runs from event handlers, where a throw would close the app.
				// Whatever the load got through stays; a pull tries again.
			}
		}
	}
}
