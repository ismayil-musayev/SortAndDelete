using SortAndDelete.Services;
using SortAndDelete.ViewModels;

namespace SortAndDelete;

public partial class App : Application
{
	private readonly GalleryService _gallery;

	public App(GalleryService gallery)
	{
		InitializeComponent();
		_gallery = gallery;

		// The whole UI is designed dark — photos pop on a dark canvas.
		UserAppTheme = AppTheme.Dark;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new AppShell());

		// Other apps can change the library while this one is in the background, so the gallery
		// reads it again when next asked, and Home reloads at once if it is showing. A new window
		// counts too: Android can destroy the activity in the background and keep the app running.
		// Not while Home shows the photo-access screen: a load asks for access, and the user said no.
		_gallery.MarkStale();
		window.Resumed += async (_, _) =>
		{
			_gallery.MarkStale();
			if (window.Page is not Shell { CurrentPage.BindingContext: HomeViewModel { HasAccess: true } home })
				return;

			try
			{
				await home.ReloadAsync();
			}
			catch (Exception)
			{
				// This runs on every return to the foreground, where a throw would close the app.
				// The page stays as it was; a pull tries again.
			}
		};
		return window;
	}
}
