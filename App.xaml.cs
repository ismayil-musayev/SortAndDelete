using SortAndDelete.Logic;
using SortAndDelete.Services;
using SortAndDelete.ViewModels;

namespace SortAndDelete;

public partial class App : Application
{
	private readonly GalleryService _gallery;
	private readonly ILibraryChangeWatcher _libraryWatcher;

	// The windows between their creation or OnRestart and their OnStop. The watcher runs while
	// there is one.
	private readonly HashSet<Window> _startedWindows = [];

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
			OnWindowStarted(window);
			await ReloadForLibraryChangeAsync([window]);
		};

		// While a window is started, from here or OnRestart (Resumed) to OnStop (Stopped), the
		// watcher reports changes as they come. A system dialog over it only pauses the activity.
		// A window destroyed without a stop counts as stopped.
		OnWindowStarted(window);
		window.Stopped += (_, _) => OnWindowStopped(window);
		window.Destroying += (_, _) => OnWindowStopped(window);
		return window;
	}

	// The watcher follows the started windows rather than each window starting and stopping it: on
	// Android 10 and 11, Back finishes the activity and stops it only later, so a quick relaunch
	// can create the next window before the last one stops.
	private void OnWindowStarted(Window window)
	{
		if (_startedWindows.Add(window) && _startedWindows.Count == 1)
			_libraryWatcher.Start();
	}

	private void OnWindowStopped(Window window)
	{
		if (_startedWindows.Remove(window) && _startedWindows.Count == 0)
			_libraryWatcher.Stop();
	}

	private async void OnLibraryChanged(object? sender, LibraryChangedEventArgs e)
	{
		// A load that began after the burst's last notification read the library with its changes
		// in it, as the loads after the app's own moves and trash requests do.
		if (ChangeDebounce.NeedsReload(e.LastNotifiedAt, _gallery.LoadStartedAt))
			await ReloadForLibraryChangeAsync([.. _startedWindows]);
	}

	/// <summary>
	/// Marks the gallery stale, so it reads the library again when next asked, and reloads Home at
	/// once in each of <paramref name="windows"/> showing it, unless Home has no photo access (see
	/// <see cref="HomeViewModel.ReloadAfterLibraryChangeAsync"/>).
	/// </summary>
	private async Task ReloadForLibraryChangeAsync(IReadOnlyList<Window> windows)
	{
		_gallery.MarkStale();
		foreach (var window in windows)
		{
			if (window.Page is not Shell { CurrentPage.BindingContext: HomeViewModel home })
				continue;

			try
			{
				await home.ReloadAfterLibraryChangeAsync();
			}
			catch (Exception)
			{
				// This runs from event handlers, where a throw would close the app.
				// Whatever the load got through stays; a pull tries again.
			}
		}
	}
}
