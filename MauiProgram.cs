using Microsoft.Extensions.Logging;
using SortAndDelete.Services;
using SortAndDelete.ViewModels;
using SortAndDelete.Views;

namespace SortAndDelete;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if DEBUG
		builder.Logging.AddDebug();
#endif

		// Platform photo library
#if ANDROID
		builder.Services.AddSingleton<IPhotoLibraryService, AndroidPhotoLibraryService>();
#elif IOS
		builder.Services.AddSingleton<IPhotoLibraryService, IosPhotoLibraryService>();
#endif

		// Shared services
		builder.Services.AddSingleton(_ =>
			new ReviewStore(Path.Combine(FileSystem.AppDataDirectory, "sortanddelete.db3")));

		// Derived data only — its own file, so it can be thrown away and rebuilt without
		// touching the review decisions, which are the user's actual work.
		builder.Services.AddSingleton(_ =>
			new SignatureStore(Path.Combine(FileSystem.AppDataDirectory, "duplicates.db3")));

		builder.Services.AddSingleton<GalleryService>();
		builder.Services.AddSingleton<DuplicateScanner>();
		builder.Services.AddSingleton<IAlbumPickerService, AlbumPickerService>();

		// View models. HomeViewModel lives as long as its page (the Shell root, so one per window):
		// a singleton would keep refilling the folder chips of a destroyed window's page, whose
		// bindings still listen until GC, and that page's handlers throw ObjectDisposedException.
		builder.Services.AddTransient<HomeViewModel>();
		builder.Services.AddTransient<SwipeViewModel>();
		builder.Services.AddTransient<BinViewModel>();

		// Singleton so the fingerprint scan survives leaving and re-entering the screen.
		builder.Services.AddSingleton<DuplicatesViewModel>();

		// Pages are transient: a page's handler belongs to the window that rendered it. Android
		// can destroy the activity (Back on Android 10/11, config changes, "Don't keep
		// activities") while the process lives on, disposing that window's services; a page
		// reused by the next window then throws ObjectDisposedException on navigation.
		builder.Services.AddTransient<HomePage>();
		builder.Services.AddTransient<SwipePage>();
		builder.Services.AddTransient<BinPage>();
		builder.Services.AddTransient<DuplicatesPage>();

		return builder.Build();
	}
}
