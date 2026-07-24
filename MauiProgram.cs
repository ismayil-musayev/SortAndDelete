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
		builder.Services.AddSingleton<GalleryService>();
		builder.Services.AddSingleton<IAlbumPickerService, AlbumPickerService>();

		// View models
		builder.Services.AddSingleton<HomeViewModel>();
		builder.Services.AddTransient<SwipeViewModel>();
		builder.Services.AddTransient<BinViewModel>();

		// Pages
		builder.Services.AddSingleton<HomePage>();
		builder.Services.AddTransient<SwipePage>();
		builder.Services.AddTransient<BinPage>();

		return builder.Build();
	}
}
